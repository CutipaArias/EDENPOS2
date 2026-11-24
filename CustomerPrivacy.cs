using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using MySqlConnector;

namespace EdenPOS
{
    // Resultado de retirar el consentimiento de un cliente (RNF-09 / RN-14).
    public sealed class ConsentRevocationResult
    {
        public string CustomerName { get; set; }
        public DateTime RevokedAt { get; set; }
        public int ReceiptsAnonymized { get; set; }
        public int ReceiptsKept { get; set; }
        public int TicketsAnonymized { get; set; }
    }

    // RNF-09 / RN-14 (Ley N.º 29733): el cliente puede retirar su consentimiento. Solo la dueña lo registra (Configuración > Clientes).
    //
    // Qué se hace, en una sola transacción:
    // - cliente: se borra el teléfono, consentimiento_datos = 0 y consentimiento_revocado = ahora. El DNI es la clave de la tabla, así que se
    //   reemplaza por un seudónimo ("X" + 7 dígitos, nunca un DNI válido de 8 dígitos). La fila queda como constancia de la revocación (fecha y
    //   nombre, que se puede registrar sin consentimiento); con el DNI real ya no se encuentra, y si el cliente vuelve y acepta de nuevo se crea
    //   un registro nuevo.
    // - recibo: se quita el DNI de los recibos de S/ 700 o menos. Los recibos mayores a S/ 700 (AppData.DniObligatorioDesde, RN-11) CONSERVAN el
    //   documento: SUNAT exige identificar al adquirente en boletas de ese monto y el recibo es el respaldo de su boleta (simulada aquí, pero el
    //   dato se conserva como lo exigiría la real), y la Ley 29733 permite conservar datos para cumplir una obligación legal. Tampoco se toca
    //   comprobante: no guarda el DNI, lo toma del recibo.
    // - ticket_guardado: se quita el DNI de las visitas abiertas; si la cuenta supera S/ 700 al cobrar, se vuelve a pedir el documento.
    // - Auditoría: CONSENTIMIENTO_RETIRADO con el nombre y los conteos, nunca con el DNI.
    // Efecto conocido: la promoción de sexta visita cuenta visitas por DNI, así que el conteo del cliente vuelve a empezar.
    public static partial class AppData
    {
        public const string AnonymizedDniPrefix = "X";

        public static bool IsAnonymizedDni(string dni)
        {
            return !string.IsNullOrEmpty(dni) && dni.StartsWith(AnonymizedDniPrefix, StringComparison.Ordinal);
        }

        // dni = clave actual del cliente (tal como está en Customers). Lanza InvalidOperationException si no existe o ya lo había retirado.
        public static ConsentRevocationResult RevokeCustomerConsent(string dni)
        {
            if (!AppSession.IsOwner) throw new InvalidOperationException("Solo la dueña puede registrar que un cliente retiró su consentimiento.");
            var key = (dni ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("Elija un cliente.");
            var result = new ConsentRevocationResult();
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = new MySqlCommand("SELECT nombre_completo, consentimiento_revocado FROM cliente WHERE dni = @dni FOR UPDATE", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@dni", key);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) throw new InvalidOperationException("El cliente ya no existe. Actualice la lista.");
                            result.CustomerName = reader.GetString(0);
                            if (!reader.IsDBNull(1)) throw new InvalidOperationException("Este cliente ya retiró su consentimiento el " + reader.GetDateTime(1).ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) + ".");
                        }
                    }

                    var now = NowWithoutFraction();
                    result.RevokedAt = now;
                    var parameters = new Dictionary<string, object> { { "@dni", key }, { "@limite", DniObligatorioDesde } };
                    // Solo los DNI reales (8 dígitos) aparecen en recibos y tickets; un seudónimo no.
                    if (!IsAnonymizedDni(key))
                    {
                        result.ReceiptsAnonymized = PrivacyCount(connection, transaction, "UPDATE recibo SET cliente_dni = NULL WHERE cliente_dni = @dni AND total <= @limite", parameters);
                        result.ReceiptsKept = Convert.ToInt32(PrivacyScalar(connection, transaction, "SELECT COUNT(*) FROM recibo WHERE cliente_dni = @dni", parameters), CultureInfo.InvariantCulture);
                        result.TicketsAnonymized = PrivacyCount(connection, transaction, "UPDATE ticket_guardado SET cliente_dni = NULL WHERE cliente_dni = @dni", parameters);
                    }

                    var pseudonym = NewDniPseudonym(connection, transaction);
                    Execute(connection, transaction,
                        "UPDATE cliente SET dni = @seudonimo, telefono = NULL, consentimiento_datos = 0, consentimiento_revocado = @fecha, updated_at = @fecha WHERE dni = @dni",
                        new Dictionary<string, object> { { "@seudonimo", pseudonym }, { "@fecha", now }, { "@dni", key } });

                    RegistrarAuditoria(connection, transaction, AuditActions.ConsentimientoRetirado,
                        "Cliente " + result.CustomerName + ": retiró su consentimiento. Teléfono borrado y DNI anonimizado en el registro de clientes, " +
                        result.ReceiptsAnonymized + " recibo(s) y " + result.TicketsAnonymized + " ticket(s) abiertos" +
                        (result.ReceiptsKept > 0 ? "; " + result.ReceiptsKept + " recibo(s) mayores a " + Theme.Money(DniObligatorioDesde) + " conservan el documento (SUNAT)." : "."));
                    transaction.Commit();
                }
                RaiseChanged();
            }
            return result;
        }

        // RNF-09: deja constancia de una exportación que incluye datos personales de clientes (solo la dueña exporta).
        public static void RegistrarExportacionDatos(string detalle)
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                RegistrarAuditoria(connection, transaction, AuditActions.DatosExportados, detalle);
                transaction.Commit();
            }
        }

        private static string NewDniPseudonym(MySqlConnection connection, MySqlTransaction transaction)
        {
            var bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create())
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    random.GetBytes(bytes);
                    var candidate = AnonymizedDniPrefix + (BitConverter.ToUInt32(bytes, 0) % 10000000u).ToString("0000000", CultureInfo.InvariantCulture);
                    var taken = Convert.ToInt64(PrivacyScalar(connection, transaction, "SELECT COUNT(*) FROM cliente WHERE dni = @dni",
                        new Dictionary<string, object> { { "@dni", candidate } }), CultureInfo.InvariantCulture);
                    if (taken == 0) return candidate;
                }
            }
            throw new InvalidOperationException("No se pudo generar un identificador anónimo para el cliente. Intente de nuevo.");
        }

        private static int PrivacyCount(MySqlConnection connection, MySqlTransaction transaction, string sql, Dictionary<string, object> parameters)
        {
            using (var command = new MySqlCommand(sql, connection, transaction))
            {
                foreach (var pair in parameters.Where(p => sql.Contains(p.Key))) command.Parameters.AddWithValue(pair.Key, pair.Value);
                return command.ExecuteNonQuery();
            }
        }

        private static object PrivacyScalar(MySqlConnection connection, MySqlTransaction transaction, string sql, Dictionary<string, object> parameters)
        {
            using (var command = new MySqlCommand(sql, connection, transaction))
            {
                foreach (var pair in parameters.Where(p => sql.Contains(p.Key))) command.Parameters.AddWithValue(pair.Key, pair.Value);
                return command.ExecuteScalar();
            }
        }
    }
}
