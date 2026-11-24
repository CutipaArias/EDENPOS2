using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MySqlConnector;

namespace EdenPOS
{
    // RF-19 / RN-06 / RN-07 / RF-22: devolución de llaves e implementos y cargos por pérdida.
    //
    // - Configuración: configuracion.cargo_perdida_llave (monto por llave no devuelta) y producto.cargo_perdida (por unidad de implemento).
    //   Solo la dueña los cambia (Configuración e Inventario son pantallas de la dueña; el setter lo vuelve a comprobar) y queda en Auditoría.
    // - Implemento: artículo de la categoría ALQUILERES o con cargo por pérdida mayor que 0. Las variantes usan el cargo del artículo.
    // - Cobro: el diálogo de devolución arma un LossReport (solo lo NO devuelto). AddReceipt vuelve a armar las líneas de cargo con los montos
    //   configurados dentro de su transacción y rechaza el cobro si las líneas recibidas no coinciden (no se confía en la UI). Las líneas de
    //   cargo no tienen id_producto: no mueven stock ni cuentan para las promociones (ni SAUNA ni personas).
    // - RN-06: una llave no devuelta NO se libera. Queda en configuracion.llaves_fuera_uso (lista "3,17") y no se puede asignar a otra visita
    //   hasta que la dueña confirme en Configuración que la repuso (ReleaseLockerOutOfService).
    public static partial class AppData
    {
        public const decimal CargoPerdidaMaximo = 1000m;
        public const string LossChargePrefix = "Cargo por pérdida";
        private const string ImplementCategory = "ALQUILERES";

        public static bool IsImplement(Product product)
        {
            return product != null && (string.Equals(product.Category, ImplementCategory, StringComparison.OrdinalIgnoreCase) || product.CargoPerdida > 0m);
        }

        // Línea de cargo por pérdida: sin artículo y con el prefijo reservado. Ninguna venta normal puede usar ese nombre.
        public static bool IsLossChargeLine(ReceiptLine line)
        {
            return line != null && string.IsNullOrEmpty(line.ProductId) &&
                   (line.Name ?? "").StartsWith(LossChargePrefix, StringComparison.OrdinalIgnoreCase);
        }

        public static string KeyChargeName(int locker)
        {
            return LossChargePrefix + " de llave N.º " + locker.ToString(CultureInfo.InvariantCulture);
        }

        public static string ImplementDisplayName(Product product, ProductOption option)
        {
            return option == null ? product.Name : product.Name + " - " + option.Name;
        }

        // ---------- Configuración (RF-22) ----------

        public static decimal CargoPerdidaLlave
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return ReadKeyCharge(connection, null);
            }
        }

        public static void SetCargoPerdidaLlave(decimal value)
        {
            if (!AppSession.IsOwner) throw new InvalidOperationException("Solo la dueña puede configurar los cargos por pérdida.");
            if (value < 0m || value > CargoPerdidaMaximo)
                throw new ArgumentException("El cargo por pérdida de llave debe estar entre 0 y " + CargoPerdidaMaximo.ToString("0", CultureInfo.InvariantCulture) + ".");
            value = Math.Round(value, 2);
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    decimal previous;
                    using (var command = new MySqlCommand("SELECT cargo_perdida_llave FROM configuracion WHERE id = 1 FOR UPDATE", connection, transaction))
                    {
                        var raw = command.ExecuteScalar();
                        if (raw == null || raw == DBNull.Value) throw new InvalidOperationException("La configuración del negocio no está inicializada.");
                        previous = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                    }
                    if (previous == value) return;
                    Execute(connection, transaction, "UPDATE configuracion SET cargo_perdida_llave = @cargo WHERE id = 1",
                        new Dictionary<string, object> { { "@cargo", value } });
                    RegistrarAuditoria(connection, transaction, AuditActions.CargoPerdidaCambiado,
                        "Cargo por pérdida de llave: " + previous.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + value.ToString("0.00", CultureInfo.InvariantCulture));
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        private static decimal ReadKeyCharge(MySqlConnection connection, MySqlTransaction transaction)
        {
            using (var command = new MySqlCommand("SELECT cargo_perdida_llave FROM configuracion WHERE id = 1", connection, transaction))
            {
                var raw = command.ExecuteScalar();
                return raw == null || raw == DBNull.Value ? 0m : Math.Min(CargoPerdidaMaximo, Math.Max(0m, Convert.ToDecimal(raw, CultureInfo.InvariantCulture)));
            }
        }

        // ---------- Llaves fuera de uso (RN-06) ----------

        public static List<int> LockersOutOfService
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return ReadLockersOutOfService(connection, null, false);
            }
        }

        // La dueña confirma que la llave se recuperó o se repuso: vuelve a estar disponible para otra visita.
        public static void ReleaseLockerOutOfService(int locker)
        {
            if (!AppSession.IsOwner) throw new InvalidOperationException("Solo la dueña puede liberar una llave fuera de uso.");
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var list = ReadLockersOutOfService(connection, transaction, true);
                    if (!list.Remove(locker)) throw new InvalidOperationException("La llave " + locker + " ya no está fuera de uso.");
                    WriteLockersOutOfService(connection, transaction, list);
                    RegistrarAuditoria(connection, transaction, AuditActions.LlaveLiberada,
                        "Llave N.º " + locker + " repuesta: vuelve a estar disponible");
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        private static List<int> ReadLockersOutOfService(MySqlConnection connection, MySqlTransaction transaction, bool forUpdate)
        {
            using (var command = new MySqlCommand("SELECT llaves_fuera_uso FROM configuracion WHERE id = 1" + (forUpdate ? " FOR UPDATE" : ""), connection, transaction))
            {
                var raw = command.ExecuteScalar();
                return ParseLockerList(raw == null || raw == DBNull.Value ? "" : Convert.ToString(raw, CultureInfo.InvariantCulture));
            }
        }

        private static void WriteLockersOutOfService(MySqlConnection connection, MySqlTransaction transaction, IEnumerable<int> lockers)
        {
            Execute(connection, transaction, "UPDATE configuracion SET llaves_fuera_uso = @lista WHERE id = 1",
                new Dictionary<string, object> { { "@lista", FormatLockerList(lockers) } });
        }

        // Tolera valores escritos a mano: ignora lo que no sea un número de 1 a 35 y los repetidos.
        internal static List<int> ParseLockerList(string text)
        {
            var result = new SortedSet<int>();
            foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int number;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 35)
                    result.Add(number);
            }
            return result.ToList();
        }

        internal static string FormatLockerList(IEnumerable<int> lockers)
        {
            return string.Join(",", (lockers ?? new int[0]).Where(x => x >= 1 && x <= 35).Distinct().OrderBy(x => x)
                .Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }

        // Al asignar llaves a una visita: rechaza las que están fuera de uso (no devueltas y sin liberar por la dueña).
        private static void EnsureLockersInService(MySqlConnection connection, MySqlTransaction transaction, IEnumerable<int> lockers)
        {
            var outOfService = ReadLockersOutOfService(connection, transaction, false);
            var blocked = (lockers ?? new int[0]).FirstOrDefault(x => outOfService.Contains(x));
            if (blocked > 0)
                throw new InvalidOperationException("La llave " + blocked + " está fuera de uso porque no se devolvió. La dueña debe liberarla en Configuración > Cargos por pérdida. Seleccione otra.");
        }

        // ---------- Líneas de cargo (RF-19 / RN-07) ----------

        // Vista previa para el diálogo de cobro: mismas reglas y montos que AddReceipt aplicará (que las vuelve a calcular).
        public static List<ReceiptLine> BuildLossChargeLines(LossReport report)
        {
            if (report == null || report.IsEmpty) return new List<ReceiptLine>();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
                return BuildLossChargeLines(ReadKeyCharge(connection, null), LoadProductsFromDb(connection, null, false), report);
        }

        private static List<ReceiptLine> BuildLossChargeLines(decimal keyCharge, List<Product> products, LossReport report)
        {
            var lines = new List<ReceiptLine>();
            if (report == null) return lines;
            foreach (var locker in (report.LostKeys ?? new List<int>()).Distinct().OrderBy(x => x))
                lines.Add(new ReceiptLine { Name = KeyChargeName(locker), UnitPrice = Math.Round(keyCharge, 2), Quantity = 1 });
            foreach (var group in GroupImplements(report))
            {
                var product = products.FirstOrDefault(x => x.Id == group.ProductId);
                if (product == null) throw new InvalidOperationException("El implemento que no se devolvió ya no existe en el catálogo.");
                var option = string.IsNullOrEmpty(group.OptionId) ? null : (product.Options ?? new List<ProductOption>()).FirstOrDefault(x => x.Id == group.OptionId);
                if (!string.IsNullOrEmpty(group.OptionId) && option == null) throw new InvalidOperationException("La variante del implemento que no se devolvió ya no existe.");
                lines.Add(new ReceiptLine
                {
                    Name = Clip(LossChargePrefix + ": " + ImplementDisplayName(product, option), 120),
                    UnitPrice = Math.Round(Math.Min(CargoPerdidaMaximo, Math.Max(0m, product.CargoPerdida)), 2),
                    Quantity = group.Quantity
                });
            }
            return lines;
        }

        private static List<LostImplement> GroupImplements(LossReport report)
        {
            return (report.LostImplements ?? new List<LostImplement>())
                .Where(x => x != null)
                .GroupBy(x => (x.ProductId ?? "") + "\u0001" + (x.OptionId ?? ""))
                .Select(g => new LostImplement { ProductId = g.First().ProductId, OptionId = string.IsNullOrEmpty(g.First().OptionId) ? null : g.First().OptionId, Quantity = g.Sum(x => x.Quantity) })
                .OrderBy(x => x.ProductId, StringComparer.Ordinal).ThenBy(x => x.OptionId ?? "", StringComparer.Ordinal)
                .ToList();
        }

        // Validación en la capa de datos, dentro de la transacción del cobro (AddReceipt):
        // - un ticket con llaves exige el informe de devolución (RF-19: se confirma antes de finalizar la visita);
        // - las llaves no devueltas deben ser del ticket; los implementos, artículos que son implemento y que están en las líneas cobradas
        //   (sin superar la cantidad cobrada);
        // - las líneas de cargo recibidas deben ser exactamente las que salen de la configuración vigente (monto, cantidad y nombre).
        private static void ValidateLossCharges(LossReport report, List<int> ticketLockers, List<ReceiptLine> regularLines, List<ReceiptLine> chargeLines,
            List<Product> products, decimal keyCharge)
        {
            if (ticketLockers.Count > 0 && report == null)
                throw new InvalidOperationException("Confirme la devolución de las llaves e implementos antes de cobrar esta visita.");
            report = report ?? new LossReport();
            var lostKeys = report.LostKeys ?? new List<int>();
            if (lostKeys.Count != lostKeys.Distinct().Count()) throw new ArgumentException("Hay llaves repetidas en la devolución.");
            var foreign = lostKeys.FirstOrDefault(x => !ticketLockers.Contains(x));
            if (lostKeys.Any(x => !ticketLockers.Contains(x)))
                throw new InvalidOperationException("La llave " + foreign + " no pertenece a esta visita.");
            foreach (var group in GroupImplements(report))
            {
                if (group.Quantity <= 0) throw new ArgumentException("La cantidad de implementos no devueltos debe ser mayor que cero.");
                var product = products.FirstOrDefault(x => x.Id == group.ProductId);
                if (product == null || !IsImplement(product)) throw new InvalidOperationException("Uno de los artículos marcados como no devueltos no es un implemento.");
                var charged = regularLines.Where(x => x.ProductId == group.ProductId && string.IsNullOrEmpty(x.OptionId) == string.IsNullOrEmpty(group.OptionId) &&
                                                      (string.IsNullOrEmpty(group.OptionId) || x.OptionId == group.OptionId)).Sum(x => x.Quantity);
                if (group.Quantity > charged)
                    throw new InvalidOperationException("No se pueden marcar más " + product.Name + " no devueltos (" + group.Quantity + ") que los entregados en la visita (" + charged + ").");
            }
            var expected = BuildLossChargeLines(keyCharge, products, report);
            Func<ReceiptLine, string> key = x => (x.Name ?? "") + "|" + Math.Round(x.UnitPrice, 2).ToString("0.00", CultureInfo.InvariantCulture) + "|" + x.Quantity.ToString(CultureInfo.InvariantCulture);
            var expectedKeys = expected.Select(key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var givenKeys = chargeLines.Select(key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!expectedKeys.SequenceEqual(givenKeys, StringComparer.Ordinal))
                throw new InvalidOperationException("Los cargos por pérdida no coinciden con los configurados (pudieron cambiar mientras se cobraba). " +
                    "No se registró el cobro: vuelva a cobrar el ticket para ver el total actualizado.");
        }

        // Tras insertar el recibo (misma transacción): llaves no devueltas fuera de uso (RN-06) y una fila de Auditoría por cada pérdida.
        private static void RegisterLosses(MySqlConnection connection, MySqlTransaction transaction, Receipt receipt, LossReport report, List<Product> products, decimal keyCharge)
        {
            if (report == null || report.IsEmpty) return;
            var lostKeys = (report.LostKeys ?? new List<int>()).Distinct().OrderBy(x => x).ToList();
            if (lostKeys.Count > 0)
            {
                var outOfService = ReadLockersOutOfService(connection, transaction, true);
                WriteLockersOutOfService(connection, transaction, outOfService.Concat(lostKeys));
                foreach (var locker in lostKeys)
                    RegistrarAuditoria(connection, transaction, AuditActions.LlavePerdida,
                        "Llave N.º " + locker + " no devuelta. Recibo #" + receipt.Number + ", cargo " + Theme.Money(keyCharge) +
                        ". Queda fuera de uso hasta que la dueña la libere.");
            }
            foreach (var group in GroupImplements(report))
            {
                var product = products.First(x => x.Id == group.ProductId);
                var option = string.IsNullOrEmpty(group.OptionId) ? null : product.Options.First(x => x.Id == group.OptionId);
                RegistrarAuditoria(connection, transaction, AuditActions.ImplementoPerdido,
                    group.Quantity + " × " + ImplementDisplayName(product, option) + " no devuelto(s). Recibo #" + receipt.Number +
                    ", cargo " + Theme.Money(Math.Round(product.CargoPerdida, 2) * group.Quantity) + ".");
            }
        }
    }
}
