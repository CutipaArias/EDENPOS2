using System;
using System.Threading;
using MySqlConnector;

namespace EdenPOS
{
    // Revisión barata de los datos compartidos: una sola consulta que combina COUNT/MAX/SUM de las tablas clave.
    // Si otra caja inserta, borra o cambia stock/precios/estados, la cadena resultante cambia. MainForm la sondea en segundo plano.
    public static partial class AppData
    {
        private const string DataRevisionSql =
            "SELECT CONCAT_WS('|', " +
            "(SELECT COUNT(*) FROM recibo), " +
            "(SELECT COALESCE(MAX(created_at), '') FROM recibo), " +
            "(SELECT COUNT(refunded_at) FROM recibo), " +
            "(SELECT COUNT(saldo_cobrado_at) FROM recibo), " +   // RN-01: cobro posterior de un saldo pendiente
            "(SELECT COUNT(*) FROM ticket_guardado), " +
            "(SELECT COALESCE(MAX(updated_at), '') FROM ticket_guardado), " +
            "(SELECT COUNT(*) FROM ticket_linea), " +
            "(SELECT COALESCE(SUM(cantidad), 0) FROM ticket_linea), " +
            "(SELECT COUNT(*) FROM casillero_llave), " +
            "(SELECT COALESCE(SUM(numero), 0) FROM casillero_llave), " +
            "(SELECT COUNT(*) FROM sesion_caja), " +
            "(SELECT COALESCE(SUM(abierta), 0) FROM sesion_caja), " +
            "(SELECT COALESCE(MAX(fecha_cierre), '') FROM sesion_caja), " +
            "(SELECT COUNT(*) FROM movimiento_caja), " +
            "(SELECT COALESCE(SUM(importe), 0) FROM movimiento_caja), " +
            "(SELECT COUNT(*) FROM producto), " +
            "(SELECT COALESCE(SUM(stock), 0) FROM producto), " +
            "(SELECT COALESCE(SUM(precio), 0) FROM producto), " +
            "(SELECT COALESCE(SUM(activo), 0) FROM producto), " +
            // Huella de las columnas de texto/ajustes (nombre, categoría, imagen, color, stock mínimo...): detecta ediciones que no cambian conteos ni sumas.
            "(SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', id_producto, categoria, nombre, COALESCE(imagen, ''), color_hex, stock_minimo, controla_inventario, tipo, cargo_perdida))), 0) FROM producto), " +
            "(SELECT COUNT(*) FROM producto_variante), " +
            "(SELECT COALESCE(SUM(stock), 0) FROM producto_variante), " +
            "(SELECT COALESCE(SUM(precio), 0) FROM producto_variante), " +
            "(SELECT COALESCE(SUM(CRC32(CONCAT_WS('|', id_variante, id_producto, nombre, orden, stock_minimo, controla_inventario))), 0) FROM producto_variante), " +
            "(SELECT COUNT(*) FROM movimiento_inventario), " +
            "(SELECT COALESCE(MAX(secuencia), 0) FROM movimiento_inventario), " +
            "(SELECT COUNT(*) FROM cliente), " +
            "(SELECT COALESCE(MAX(updated_at), '') FROM cliente))";

        // Devuelve la "huella" actual de los datos compartidos. Lanza MySqlException si el servidor no responde.
        // Bloquea: llamar solo desde un hilo de fondo, nunca desde el hilo de la interfaz.
        public static string GetDataRevision()
        {
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(DataRevisionSql, connection))
            {
                command.CommandTimeout = 10;
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? "" : Convert.ToString(value);
            }
        }
    }

    // Sondeo periódico de la revisión. Nunca toca la interfaz ni MySQL en el hilo de la UI: avisa por callback desde un hilo de fondo.
    public sealed class DataRevisionPoller : IDisposable
    {
        private readonly Timer timer;
        private readonly Action<string> changed;
        private readonly int intervalMs;
        private int running;       // evita solapar sondeos si MySQL tarda
        private int disposed;
        private int localDirty;    // hubo un cambio local desde el último sondeo: la diferencia se adopta sin refrescar
        private string baseline;
        private bool failing;

        public DataRevisionPoller(Action<string> changed, int intervalMs)
        {
            this.changed = changed;
            this.intervalMs = intervalMs;
            timer = new Timer(Tick, null, intervalMs, intervalMs);
        }

        // La vista ya se refrescó (o se decidió no hacerlo): esta revisión pasa a ser la base.
        public void Accept(string revision)
        {
            baseline = revision;
        }

        // Cambio hecho por esta misma caja: se adelanta el sondeo para recalcular la base sin refrescar dos veces.
        public void NotifyLocalChange()
        {
            Interlocked.Exchange(ref localDirty, 1);
            if (Volatile.Read(ref disposed) == 0)
                try { timer.Change(400, intervalMs); } catch (ObjectDisposedException) { }
        }

        private void Tick(object state)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                var wasLocal = Interlocked.Exchange(ref localDirty, 0) == 1;
                string revision;
                try
                {
                    revision = AppData.GetDataRevision();
                    failing = false;
                }
                catch (Exception ex)
                {
                    // Servidor caído o sin red: en silencio, se registra solo el primer fallo de la racha y se reintenta en el siguiente ciclo.
                    if (!failing)
                    {
                        failing = true;
                        try { AppData.LogError(ex, "Sondeo de cambios de otras cajas"); } catch { }
                    }
                    if (wasLocal) Interlocked.Exchange(ref localDirty, 1);
                    return;
                }
                if (Volatile.Read(ref disposed) != 0) return;
                if (baseline == null || wasLocal) { baseline = revision; return; }   // primera lectura, o cambio propio
                if (revision == baseline) return;
                changed(revision);   // MainForm decide si refresca (y llama a Accept) o lo pospone dejando la base intacta
            }
            catch { }
            finally { Interlocked.Exchange(ref running, 0); }
        }


        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            timer.Dispose();
        }
    }
}
