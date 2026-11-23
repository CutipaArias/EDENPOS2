using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace EdenPOS
{
    // ===== Facturación electrónica SIMULADA (módulo e-invoicing) =====
    // Nada de este módulo se envía a SUNAT ni a un OSE/PSE: el emisor incluido (EmisorElectronicoStub) es un adaptador simulado
    // que acepta o rechaza en local. No hay credenciales de SUNAT/OSE en ninguna parte. Un emisor real se conectaría
    // implementando IEmisorElectronico y asignándolo a ElectronicInvoicingService.Emisor; el resto (tabla comprobante, estados,
    // reintentos, auditoría) no cambiaría.

    public static class ComprobanteEstados
    {
        public const string Pendiente = "Pendiente";
        public const string Aceptado = "Aceptado";
        public const string Rechazado = "Rechazado";
        // Reembolsado el recibo: el comprobante queda anulado (no se crea nota de crédito real; ver AppData.AnularComprobante).
        public const string Anulado = "Anulado";
    }

    public sealed class Comprobante
    {
        public string Id { get; set; }
        public string ReceiptId { get; set; }
        public string Serie { get; set; }
        public int Numero { get; set; }
        public DateTime FechaEmision { get; set; }
        public string Estado { get; set; }
        public string CodigoRespuesta { get; set; }
        public int Intentos { get; set; }
        public DateTime? UltimoIntento { get; set; }
        // Datos del recibo, útiles para el emisor (se rellenan al leer con ReadComprobantes).
        public int ReceiptNumber { get; set; }
        public decimal Total { get; set; }
        public string CustomerName { get; set; }
        public string CustomerDni { get; set; }

        // "B001-000123"
        public string Documento { get { return Serie + "-" + Numero.ToString("000000", CultureInfo.InvariantCulture); } }
    }

    public sealed class ResultadoEnvio
    {
        public string Estado { get; set; }
        public string Codigo { get; set; }
        public string Mensaje { get; set; }
        public bool Aceptado { get { return Estado == ComprobanteEstados.Aceptado; } }

        public static ResultadoEnvio Acepta(string codigo, string mensaje)
        {
            return new ResultadoEnvio { Estado = ComprobanteEstados.Aceptado, Codigo = codigo, Mensaje = mensaje };
        }

        public static ResultadoEnvio Rechaza(string codigo, string mensaje)
        {
            return new ResultadoEnvio { Estado = ComprobanteEstados.Rechazado, Codigo = codigo, Mensaje = mensaje };
        }
    }

    /// <summary>
    /// Punto de conexión con el servicio de emisión de boletas (patrón Adapter). Hoy lo implementa
    /// <see cref="EmisorElectronicoStub"/> (simulado); un emisor real de SUNAT/OSE solo debe implementar esta interfaz.
    /// </summary>
    public interface IEmisorElectronico
    {
        /// <summary>Envía el comprobante y devuelve si fue aceptado, rechazado o quedó pendiente.</summary>
        Task<ResultadoEnvio> EnviarAsync(Comprobante comprobante);

        /// <summary>Consulta el estado de un comprobante ya enviado, por serie y número.</summary>
        Task<ResultadoEnvio> ConsultarEstadoAsync(string serie, int numero);
    }

    // Adaptador SIMULADO: acepta al instante, o rechaza con un código de prueba si la opción "Simular rechazo" está activa.
    public sealed class EmisorElectronicoStub : IEmisorElectronico
    {
        public const string CodigoAceptado = "SIM-OK";
        public const string CodigoRechazado = "SIM-RECH";
        private readonly Func<bool> simularRechazo;

        public EmisorElectronicoStub(Func<bool> simularRechazo)
        {
            this.simularRechazo = simularRechazo ?? (() => false);
        }

        public Task<ResultadoEnvio> EnviarAsync(Comprobante comprobante)
        {
            if (comprobante == null) throw new ArgumentNullException("comprobante");
            return Task.FromResult(simularRechazo()
                ? ResultadoEnvio.Rechaza(CodigoRechazado, "Rechazo simulado: no se envió nada a SUNAT.")
                : ResultadoEnvio.Acepta(CodigoAceptado, "Aceptado de forma simulada: no se envió nada a SUNAT."));
        }

        public Task<ResultadoEnvio> ConsultarEstadoAsync(string serie, int numero)
        {
            // El stub no guarda estado propio: la verdad está en la tabla comprobante.
            return Task.FromResult(simularRechazo()
                ? ResultadoEnvio.Rechaza(CodigoRechazado, "Consulta simulada.")
                : ResultadoEnvio.Acepta(CodigoAceptado, "Consulta simulada."));
        }
    }

    public sealed class ResumenComprobantes
    {
        public int Pendiente { get; set; }
        public int Aceptado { get; set; }
        public int Rechazado { get; set; }
        public int Anulado { get; set; }
        public int Total { get { return Pendiente + Aceptado + Rechazado + Anulado; } }
    }

    public sealed class ResultadoReintento
    {
        public bool YaEnCurso { get; set; }
        public int Procesados { get; set; }
        public int Aceptados { get; set; }
        public int Rechazados { get; set; }
    }

    // Envío en segundo plano y reintento periódico mientras la app está abierta (RF-21).
    public static class ElectronicInvoicingService
    {
        public const int MaxIntentos = 10;
        public static readonly TimeSpan IntervaloPorDefecto = TimeSpan.FromMinutes(5);
        private static readonly object TimerLock = new object();
        private static Timer timer;
        private static IEmisorElectronico emisor;

        // Emisor actual. Por defecto el stub simulado; un emisor real se asignaría aquí.
        public static IEmisorElectronico Emisor
        {
            get { lock (TimerLock) { return emisor ?? (emisor = new EmisorElectronicoStub(() => AppData.SimularRechazoComprobantes)); } }
            set { lock (TimerLock) emisor = value; }
        }

        // Inicia el reintento periódico (cada 5 minutos por defecto; el intervalo es configurable para pruebas). Llamarlo otra vez lo reinicia.
        public static void Start(TimeSpan? interval = null)
        {
            var period = interval ?? IntervaloPorDefecto;
            if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("interval");
            lock (TimerLock)
            {
                if (timer != null) { timer.Dispose(); timer = null; }
                // Primer disparo a los 30 s (o antes, si el intervalo es menor); después cada "period".
                var first = period < TimeSpan.FromSeconds(30) ? period : TimeSpan.FromSeconds(30);
                timer = new Timer(OnTick, null, first, period);
            }
        }

        public static void Stop()
        {
            lock (TimerLock)
            {
                if (timer != null) { timer.Dispose(); timer = null; }
            }
        }

        private static void OnTick(object state)
        {
            try { AppData.ReintentarComprobantes(false); }
            catch (Exception ex) { AppData.LogError(ex, "Reintento de comprobantes"); }
        }

        // Envía un comprobante recién creado sin bloquear la UI ni la transacción de la venta.
        public static void EnviarEnSegundoPlano(string comprobanteId)
        {
            Task.Run(() =>
            {
                try { AppData.ProcesarComprobante(comprobanteId, true); }
                catch (Exception ex) { AppData.LogError(ex, "Envío de comprobante en segundo plano"); }
            });
        }
    }

    public static partial class AppData
    {
        // RN-11: ventas con total (ya con descuento) mayor a este monto exigen DNI del cliente.
        public const decimal DniObligatorioDesde = 700m;
        public const string SerieBoleta = "B001";
        private const string ComprobanteColumnas = "id_comprobante, id_recibo, serie, numero, fecha_emision, estado_envio, codigo_respuesta, intentos, ultimo_intento";
        private const string SimularRechazoKey = "SimulateInvoiceRejection";
        private static readonly object SendGate = new object();
        private static int retryRunning;
        private static int simularRechazoCache = -1;

        // "Simular rechazo": se guarda en el settings.json local (no hay columna en la base y no cambia el esquema).
        // Cambió el estado de algún comprobante. Puede dispararse desde un hilo de fondo.
        public static event EventHandler ComprobantesChanged;

        private static void RaiseComprobantesChanged()
        {
            try
            {
                var handler = ComprobantesChanged;
                if (handler != null) handler(null, EventArgs.Empty);
            }
            catch (Exception ex) { LogError(ex, "Actualizar vistas tras enviar comprobante"); }
        }

        public static bool SimularRechazoComprobantes
        {
            get
            {
                var cached = simularRechazoCache;
                if (cached >= 0) return cached == 1;
                bool value = false;
                lock (Sync)
                {
                    try
                    {
                        if (File.Exists(PathFor("settings")))
                        {
                            var data = Json.DeserializeObject(File.ReadAllText(PathFor("settings"), Encoding.UTF8)) as Dictionary<string, object>;
                            object raw;
                            if (data != null && data.TryGetValue(SimularRechazoKey, out raw) && raw is bool) value = (bool)raw;
                        }
                    }
                    catch { }
                }
                simularRechazoCache = value ? 1 : 0;
                return value;
            }
            set
            {
                lock (Sync)
                {
                    // Lectura estricta: si settings.json no se puede leer lanza y no escribe (no se pierden claves como la cadena de conexión).
                    var data = ReadLocalSettingsStrict();
                    data[SimularRechazoKey] = value;
                    Save("settings", data);
                    simularRechazoCache = value ? 1 : 0;
                }
            }
        }

        private static Comprobante ReadComprobanteRow(MySqlDataReader reader)
        {
            var c = new Comprobante
            {
                Id = reader.GetString(0),
                ReceiptId = reader.GetString(1),
                Serie = reader.GetString(2),
                Numero = reader.GetInt32(3),
                FechaEmision = reader.GetDateTime(4),
                Estado = reader.GetString(5),
                CodigoRespuesta = reader.IsDBNull(6) ? null : reader.GetString(6),
                Intentos = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                UltimoIntento = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8)
            };
            if (reader.FieldCount > 9)
            {
                c.ReceiptNumber = reader.GetInt32(9);
                c.Total = reader.GetDecimal(10);
                c.CustomerName = reader.GetString(11);
                c.CustomerDni = reader.IsDBNull(12) ? "" : reader.GetString(12);
            }
            return c;
        }

        // Comprobantes con los datos del recibo. where = condición SQL con parámetros (sin valores concatenados).
        private static List<Comprobante> ReadComprobantes(MySqlConnection connection, MySqlTransaction transaction, string where, Dictionary<string, object> parameters)
        {
            var list = new List<Comprobante>();
            using (var command = new MySqlCommand(
                "SELECT c.id_comprobante, c.id_recibo, c.serie, c.numero, c.fecha_emision, c.estado_envio, c.codigo_respuesta, c.intentos, c.ultimo_intento, " +
                "r.numero, r.total, r.cliente_nombre, r.cliente_dni " +
                "FROM comprobante c JOIN recibo r ON r.id_recibo = c.id_recibo " + where + " ORDER BY c.serie, c.numero", connection, transaction))
            {
                foreach (var p in parameters) command.Parameters.AddWithValue(p.Key, p.Value);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) list.Add(ReadComprobanteRow(reader));
            }
            return list;
        }

        // Comprobantes de los recibos leídos por ReadReceipts (misma conexión/transacción; filtro por recibo o todos).
        private static void AttachComprobantes(MySqlConnection connection, MySqlTransaction transaction, string receiptId, Dictionary<string, Receipt> byId)
        {
            using (var command = new MySqlCommand(
                "SELECT " + ComprobanteColumnas + " FROM comprobante" + (receiptId == null ? "" : " WHERE id_recibo = @id"), connection, transaction))
            {
                if (receiptId != null) command.Parameters.AddWithValue("@id", receiptId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var c = ReadComprobanteRow(reader);
                        Receipt owner;
                        if (byId.TryGetValue(c.ReceiptId, out owner)) owner.Comprobante = c;
                    }
                }
            }
        }

        // Dentro de la transacción de AddReceipt. La numeración es correlativa por serie: MAX(numero)+1 con FOR UPDATE.
        // La transacción ya tiene bloqueada la fila de configuración (y AddReceipt corre dentro de lock (Sync)), así que dos cobros
        // no pueden calcular el mismo número; la clave única (serie, numero) lo respalda como última defensa.
        private static Comprobante CrearComprobante(MySqlConnection connection, MySqlTransaction transaction, Receipt receipt)
        {
            int numero;
            using (var command = new MySqlCommand("SELECT COALESCE(MAX(numero), 0) + 1 FROM comprobante WHERE serie = @serie FOR UPDATE", connection, transaction))
            {
                command.Parameters.AddWithValue("@serie", SerieBoleta);
                numero = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var comprobante = new Comprobante
            {
                Id = Guid.NewGuid().ToString("N"),
                ReceiptId = receipt.Id,
                Serie = SerieBoleta,
                Numero = numero,
                FechaEmision = receipt.CreatedAt,
                Estado = ComprobanteEstados.Pendiente,
                Intentos = 0,
                ReceiptNumber = receipt.Number,
                Total = receipt.Total,
                CustomerName = receipt.CustomerName,
                CustomerDni = receipt.CustomerDni
            };
            Execute(connection, transaction,
                "INSERT INTO comprobante (id_comprobante, id_recibo, serie, numero, fecha_emision, estado_envio, codigo_respuesta, intentos, ultimo_intento) " +
                "VALUES (@id, @recibo, @serie, @numero, @fecha, @estado, NULL, 0, NULL)",
                new Dictionary<string, object>
                {
                    { "@id", comprobante.Id }, { "@recibo", comprobante.ReceiptId }, { "@serie", comprobante.Serie },
                    { "@numero", comprobante.Numero }, { "@fecha", comprobante.FechaEmision }, { "@estado", comprobante.Estado }
                });
            return comprobante;
        }

        // Decisión (reembolso): no hay nota de crédito real en un emisor simulado. Al reembolsar un recibo, su comprobante pasa a
        // 'Anulado' (sea cual fuera su estado) para que el reintento no lo envíe después, y la auditoría del reembolso lo deja escrito.
        // Devuelve "B001-000123" si había comprobante que anular, o null.
        private static string AnularComprobante(MySqlConnection connection, MySqlTransaction transaction, string receiptId)
        {
            var actuales = ReadComprobantes(connection, transaction, "WHERE c.id_recibo = @id AND c.estado_envio <> 'Anulado'", new Dictionary<string, object> { { "@id", receiptId } });
            if (actuales.Count == 0) return null;
            Execute(connection, transaction,
                "UPDATE comprobante SET estado_envio = 'Anulado' WHERE id_recibo = @id AND estado_envio <> 'Anulado'",
                new Dictionary<string, object> { { "@id", receiptId } });
            return actuales[0].Documento + " (estaba " + actuales[0].Estado + ")";
        }

        public static Comprobante GetComprobante(string receiptId)
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                var list = ReadComprobantes(connection, null, "WHERE c.id_recibo = @id", new Dictionary<string, object> { { "@id", receiptId ?? "" } });
                return list.Count == 0 ? null : list[0];
            }
        }

        public static List<Comprobante> GetComprobantes()
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
                return ReadComprobantes(connection, null, "", new Dictionary<string, object>());
        }

        public static ResumenComprobantes GetComprobantesResumen()
        {
            var resumen = new ResumenComprobantes();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand("SELECT estado_envio, COUNT(*) FROM comprobante GROUP BY estado_envio", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var count = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    switch (reader.GetString(0))
                    {
                        case ComprobanteEstados.Pendiente: resumen.Pendiente = count; break;
                        case ComprobanteEstados.Aceptado: resumen.Aceptado = count; break;
                        case ComprobanteEstados.Rechazado: resumen.Rechazado = count; break;
                        case ComprobanteEstados.Anulado: resumen.Anulado = count; break;
                    }
                }
            }
            return resumen;
        }

        // Envía un comprobante al emisor y guarda el resultado. Devuelve el estado final, o null si no se envió
        // (no existe, ya está resuelto/anulado o llegó al tope de intentos). Los envíos se serializan con SendGate; la llamada al emisor
        // va fuera de lock (Sync) para no bloquear la app, y la actualización solo toca comprobantes aún Pendiente/Rechazado
        // (un reembolso que ocurra mientras tanto lo deja Anulado).
        public static string ProcesarComprobante(string comprobanteId, bool ignorarTope)
        {
            return ProcesarComprobante(comprobanteId, ignorarTope, true);
        }

        // Los cambios de comprobantes avisan por ComprobantesChanged (no por DataChanged): así Configuración no recarga ni pierde lo que se está escribiendo.
        // Pueden llegar desde un hilo de fondo: quien se suscriba debe pasar al hilo de la interfaz.
        private static string ProcesarComprobante(string comprobanteId, bool ignorarTope, bool avisar)
        {
            lock (SendGate)
            {
                Comprobante comprobante;
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                {
                    var list = ReadComprobantes(connection, null, "WHERE c.id_comprobante = @id", new Dictionary<string, object> { { "@id", comprobanteId ?? "" } });
                    comprobante = list.Count == 0 ? null : list[0];
                }
                if (comprobante == null) return null;
                if (comprobante.Estado != ComprobanteEstados.Pendiente && comprobante.Estado != ComprobanteEstados.Rechazado) return null;
                if (!ignorarTope && comprobante.Intentos >= ElectronicInvoicingService.MaxIntentos) return null;

                ResultadoEnvio resultado;
                try
                {
                    resultado = ElectronicInvoicingService.Emisor.EnviarAsync(comprobante).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LogError(ex, "Emisor electrónico");
                    resultado = ResultadoEnvio.Rechaza("ERR-EMISOR", ex.Message);
                }
                if (resultado == null || (resultado.Estado != ComprobanteEstados.Aceptado && resultado.Estado != ComprobanteEstados.Rechazado))
                    resultado = ResultadoEnvio.Rechaza(resultado != null && !string.IsNullOrEmpty(resultado.Codigo) ? resultado.Codigo : "ERR-EMISOR", "Respuesta no válida del emisor.");

                var estadoFinal = resultado.Estado;
                lock (Sync)
                {
                    using (var connection = SqlConnectionFactory.Open())
                    using (var transaction = connection.BeginTransaction())
                    {
                        int updated;
                        using (var command = new MySqlCommand(
                            "UPDATE comprobante SET estado_envio = @estado, codigo_respuesta = @codigo, intentos = LEAST(intentos + 1, 100), ultimo_intento = @fecha " +
                            "WHERE id_comprobante = @id AND estado_envio IN ('Pendiente', 'Rechazado')", connection, transaction))
                        {
                            command.Parameters.AddWithValue("@estado", estadoFinal);
                            command.Parameters.AddWithValue("@codigo", string.IsNullOrEmpty(resultado.Codigo) ? (object)DBNull.Value : Clip(resultado.Codigo, 10));
                            command.Parameters.AddWithValue("@fecha", NowWithoutFraction());
                            command.Parameters.AddWithValue("@id", comprobante.Id);
                            updated = command.ExecuteNonQuery();
                        }
                        if (updated == 0) return null; // se anuló (reembolso) mientras se enviaba
                        if (estadoFinal == ComprobanteEstados.Rechazado)
                            RegistrarAuditoria(connection, transaction, AuditActions.ComprobanteRechazado,
                                "Comprobante " + comprobante.Documento + " (recibo #" + comprobante.ReceiptNumber + ") rechazado (simulado): código " +
                                (resultado.Codigo ?? "?") + ", intento " + (comprobante.Intentos + 1) + (string.IsNullOrEmpty(resultado.Mensaje) ? "" : ". " + resultado.Mensaje), true);
                        transaction.Commit();
                    }
                }
                if (avisar) RaiseComprobantesChanged();
                return estadoFinal;
            }
        }

        // Reintenta los comprobantes Pendiente/Rechazado. ignorarTope=false (el temporizador) respeta MaxIntentos; el botón
        // "Reintentar envíos" lo ignora para poder destrabar uno que llegó al tope. No corre dos veces a la vez.
        public static ResultadoReintento ReintentarComprobantes(bool ignorarTope)
        {
            var resultado = new ResultadoReintento();
            if (Interlocked.CompareExchange(ref retryRunning, 1, 0) != 0)
            {
                resultado.YaEnCurso = true;
                return resultado;
            }
            try
            {
                var ids = new List<string>();
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                using (var command = new MySqlCommand(
                    "SELECT id_comprobante FROM comprobante WHERE estado_envio IN ('Pendiente', 'Rechazado')" +
                    (ignorarTope ? "" : " AND intentos < @tope") + " ORDER BY serie, numero", connection))
                {
                    if (!ignorarTope) command.Parameters.AddWithValue("@tope", ElectronicInvoicingService.MaxIntentos);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) ids.Add(reader.GetString(0));
                }
                foreach (var id in ids)
                {
                    var estado = ProcesarComprobante(id, ignorarTope, false);
                    if (estado == null) continue;
                    resultado.Procesados++;
                    if (estado == ComprobanteEstados.Aceptado) resultado.Aceptados++; else resultado.Rechazados++;
                }
                if (resultado.Procesados > 0) RaiseComprobantesChanged(); // un solo aviso al final
                return resultado;
            }
            finally
            {
                Interlocked.Exchange(ref retryRunning, 0);
            }
        }
    }
}
