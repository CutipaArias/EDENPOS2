using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MySqlConnector;

namespace EdenPOS
{
    // RF-17: reporte de visitas por rango de fechas (Reportes > Visitas).
    public sealed class VisitDayRow
    {
        public DateTime Day { get; set; }
        public int Visits { get; set; }
        public int Adults { get; set; }
        public int Children { get; set; }
        public int People { get { return Adults + Children; } }
        public decimal Total { get; set; }
        public decimal PendingBalance { get; set; }
        // Promedio en minutos de las visitas del día con hora de ingreso (null si ninguna la tiene).
        public double? AverageMinutes { get; set; }
    }

    public sealed class VisitReport
    {
        public int Visits;
        public int Adults;
        public int Children;
        public int People { get { return Adults + Children; } }
        public decimal Total;
        // Saldo que aún se debe de las visitas del rango (sin contar lo ya cobrado ni los recibos reembolsados).
        public decimal PendingBalance;
        public int PendingCount;
        // Visitas con hora de ingreso (venían de un ticket guardado) y su duración promedio ingreso -> cobro.
        public int TimedVisits;
        public double? AverageMinutes;
        public List<VisitDayRow> ByDay = new List<VisitDayRow>();
    }

    // RN-01: saldo pendiente autorizado por la dueña y su cobro posterior. RF-17: visitas.
    //
    // Decisiones:
    // - recibo.total sigue siendo el importe de la venta; recibo.saldo_pendiente es lo que quedó sin pagar al cerrar la visita, así que lo
    //   pagado en la visita es total - saldo_pendiente (monto_efectivo / monto_yape guardan solo lo pagado). saldo_pendiente no cambia al
    //   cobrar el saldo: el pago posterior va en saldo_cobrado_* (fecha, medio, efectivo, Yape, quién y turno) y "lo que se debe" es
    //   saldo_pendiente mientras saldo_cobrado_at sea NULL.
    // - Caja esperada: solo entra lo pagado. El efectivo de la visita cuenta en el turno del recibo; el del saldo, en el turno en que se cobra.
    // - Reembolso: devuelve solo lo pagado (Receipt.TotalPaid) y anula la deuda que quedaba.
    // - Un recibo con saldo sigue en estado Pagado: cuenta como visita para la sexta visita gratis (RN-03) y para este reporte.
    public static partial class AppData
    {
        // Visita = recibo Pagado que venía de un ticket guardado (ingreso_at no nulo) o con al menos una línea de categoría SAUNA (mismo
        // criterio que la sexta visita en EvaluatePromotion). Las variantes conservan id_producto, así que heredan la categoría del artículo.
        private const string VisitCondition =
            "r.estado = 'Pagado' AND r.created_at >= @desde AND r.created_at < @hasta AND (r.ingreso_at IS NOT NULL OR EXISTS (" +
            "SELECT 1 FROM recibo_linea vl INNER JOIN producto vp ON vp.id_producto = vl.id_producto " +
            "WHERE vl.id_recibo = r.id_recibo AND vp.categoria = 'SAUNA'))";

        // ---------- Autorización de la dueña (RN-01) ----------

        // Dueñas activas que pueden autorizar un saldo pendiente (para el selector del diálogo).
        public static List<AppUser> ActiveOwners
        {
            get { return Users.Where(x => x.Active && x.Role == UserRoles.Owner).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        // Comprobación previa del diálogo: usa TryLogin, así los PIN equivocados suman intentos y bloquean la cuenta igual que al iniciar sesión.
        // Devuelve null si la dueña autoriza o el mensaje para mostrar.
        public static string CheckOwnerAuthorization(string ownerUserId, string pin)
        {
            var owner = Users.FirstOrDefault(x => x.Id == ownerUserId);
            if (owner == null || !owner.Active || owner.Role != UserRoles.Owner) return "Elija una dueña activa para autorizar.";
            var result = TryLogin(ownerUserId, pin);
            switch (result.Outcome)
            {
                case LoginOutcome.Success: return null;
                case LoginOutcome.Locked:
                case LoginOutcome.JustLocked: return UserSecurity.LockedMessage(result.User);
                default:
                    return "PIN incorrecto." + (result.AttemptsLeft > 0 ? " Le quedan " + result.AttemptsLeft + (result.AttemptsLeft == 1 ? " intento." : " intentos.") : "");
            }
        }

        // Dentro de la transacción del cobro: la autorización vale solo si es de una dueña activa, no bloqueada y con su PIN correcto.
        private static AppUser VerifyOwnerAuthorization(MySqlConnection connection, MySqlTransaction transaction, string ownerUserId, string pin)
        {
            int id;
            if (!int.TryParse(ownerUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                throw new InvalidOperationException("El saldo pendiente necesita la autorización de la dueña. No se registró el cobro.");
            var user = LoadUsersFromDb(connection, transaction, id, false).FirstOrDefault();
            if (user == null || !user.Active || user.Role != UserRoles.Owner)
                throw new InvalidOperationException("Solo una dueña activa puede autorizar un saldo pendiente. No se registró el cobro.");
            if (UserSecurity.IsLocked(user))
                throw new InvalidOperationException(UserSecurity.LockedMessage(user) + " No se registró el cobro.");
            if (!UserSecurity.VerifyPin(user, pin))
                throw new InvalidOperationException("El PIN de la dueña no es correcto. No se registró el cobro.");
            return user;
        }

        // ---------- Cobro posterior del saldo (RN-01) ----------

        // Registra el pago del saldo de un recibo. Entra al turno abierto como cobro del día en que se paga (caja esperada y resumen de cobros).
        // Se cobra el saldo completo: Efectivo (received >= saldo), Yape o Mixto (efectivo + Yape = saldo).
        public static Receipt CollectBalance(string receiptId, string method, decimal received, decimal cashAmount = -1m, decimal yapeAmount = -1m)
        {
            if (string.IsNullOrWhiteSpace(receiptId)) throw new ArgumentException("Seleccione un recibo.");
            if (method != "Efectivo" && method != "Yape" && method != "Mixto") throw new ArgumentException("Seleccione un medio de pago.");
            lock (Sync)
            {
                Receipt stored;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    decimal balance;
                    string status;
                    bool collected;
                    using (var command = new MySqlCommand(
                        "SELECT estado, saldo_pendiente, saldo_cobrado_at FROM recibo WHERE id_recibo = @id FOR UPDATE", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", receiptId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) throw new InvalidOperationException("El recibo ya no existe.");
                            status = reader.GetString(0);
                            balance = reader.GetDecimal(1);
                            collected = !reader.IsDBNull(2);
                        }
                    }
                    if (status != "Pagado") throw new InvalidOperationException("El recibo está " + status.ToLowerInvariant() + ": su saldo ya no se puede cobrar.");
                    if (balance <= 0m) throw new InvalidOperationException("Este recibo no tiene saldo pendiente.");
                    if (collected) throw new InvalidOperationException("El saldo de este recibo ya se cobró.");
                    // RN-02: igual que AddReceipt, LOCK IN SHARE MODE (MariaDB 10.4) hace esperar a un cierre de caja simultáneo, así el saldo entra en ese turno.
                    var shift = ReadShifts(connection, transaction, "WHERE abierta = 1 ORDER BY fecha_apertura DESC LIMIT 1 LOCK IN SHARE MODE", new Dictionary<string, object>()).FirstOrDefault();
                    if (shift == null) throw new InvalidOperationException("Abra un turno antes de cobrar el saldo: el pago entra a la caja abierta.");
                    var cash = Math.Round(method == "Mixto" ? cashAmount : method == "Efectivo" ? balance : 0m, 2);
                    var yape = Math.Round(method == "Mixto" ? yapeAmount : method == "Yape" ? balance : 0m, 2);
                    if (method == "Efectivo" && Math.Round(received, 2) < balance)
                        throw new ArgumentException("El efectivo recibido (" + Theme.Money(received) + ") es menor que el saldo (" + Theme.Money(balance) + ").");
                    if (method == "Mixto" && (cashAmount < 0m || yapeAmount < 0m || cash + yape != balance))
                        throw new ArgumentException("En pago mixto, efectivo más Yape debe sumar exactamente el saldo (" + Theme.Money(balance) + ").");
                    var employee = Clip(string.IsNullOrWhiteSpace(AppSession.UserName) ? shift.Employee : AppSession.UserName, 60);
                    Execute(connection, transaction,
                        "UPDATE recibo SET saldo_cobrado_at = @fecha, saldo_cobrado_medio = @medio, saldo_cobrado_efectivo = @efectivo, " +
                        "saldo_cobrado_yape = @yape, saldo_cobrado_por = @empleado, saldo_id_sesion = @sesion WHERE id_recibo = @id",
                        new Dictionary<string, object>
                        {
                            { "@fecha", NowWithoutFraction() }, { "@medio", method }, { "@efectivo", cash }, { "@yape", yape },
                            { "@empleado", employee }, { "@sesion", shift.Id }, { "@id", receiptId }
                        });
                    stored = ReadReceipts(connection, transaction, receiptId).First();
                    RegistrarAuditoria(connection, transaction, AuditActions.SaldoCobrado,
                        "Recibo #" + stored.Number + " (" + stored.CustomerName + "): saldo " + Theme.Money(balance) + " cobrado con " + method +
                        (method == "Mixto" ? " (efectivo " + Theme.Money(cash) + ", Yape " + Theme.Money(yape) + ")" : "") +
                        " en el turno " + shift.Number + ". Lo había autorizado " + (stored.BalanceAuthorizedBy ?? "—") + ".");
                    transaction.Commit();
                }
                RaiseChanged();
                return stored;
            }
        }

        // ---------- Reporte de visitas (RF-17) ----------

        public static VisitReport GetVisitReport(DateTime desde, DateTime hasta)
        {
            var report = new VisitReport();
            var visits = new List<Tuple<string, DateTime, DateTime?, decimal, decimal>>();
            var lines = new Dictionary<string, List<ReceiptLine>>();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                using (var command = new MySqlCommand(
                    "SELECT r.id_recibo, r.created_at, r.ingreso_at, r.total, CASE WHEN r.saldo_cobrado_at IS NULL THEN r.saldo_pendiente ELSE 0 END " +
                    "FROM recibo r WHERE " + VisitCondition + " ORDER BY r.created_at, r.numero", connection))
                {
                    ReportRange(desde, hasta, command);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            visits.Add(Tuple.Create(reader.GetString(0), reader.GetDateTime(1), reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                                reader.GetDecimal(3), reader.GetDecimal(4)));
                }
                if (visits.Count == 0) return report;
                using (var command = new MySqlCommand(
                    "SELECT l.id_recibo, l.id_producto, l.nombre, l.cantidad FROM recibo_linea l INNER JOIN recibo r ON r.id_recibo = l.id_recibo " +
                    "WHERE " + VisitCondition + " ORDER BY l.id_recibo, l.orden", connection))
                {
                    ReportRange(desde, hasta, command);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            List<ReceiptLine> list;
                            var id = reader.GetString(0);
                            if (!lines.TryGetValue(id, out list)) lines[id] = list = new List<ReceiptLine>();
                            list.Add(new ReceiptLine { ProductId = reader.IsDBNull(1) ? null : reader.GetString(1), Name = reader.GetString(2), Quantity = reader.GetInt32(3) });
                        }
                }
            }
            return BuildVisitReport(visits, lines);
        }

        // Agregación separada de la consulta (personas con OccupancyCalculator, como el descuento grupal y los tickets guardados).
        private static VisitReport BuildVisitReport(List<Tuple<string, DateTime, DateTime?, decimal, decimal>> visits, Dictionary<string, List<ReceiptLine>> lines)
        {
            var report = new VisitReport();
            var minutes = new List<double>();
            var days = new SortedDictionary<DateTime, VisitDayRow>();
            var dayMinutes = new Dictionary<DateTime, List<double>>();
            foreach (var visit in visits)
            {
                List<ReceiptLine> visitLines;
                lines.TryGetValue(visit.Item1, out visitLines);
                int adults, children;
                OccupancyCalculator.Count((visitLines ?? new List<ReceiptLine>()).Where(x => !IsLossChargeLine(x)), out adults, out children);
                var day = visit.Item2.Date;
                VisitDayRow row;
                if (!days.TryGetValue(day, out row)) days[day] = row = new VisitDayRow { Day = day };
                row.Visits++;
                row.Adults += adults;
                row.Children += children;
                row.Total += visit.Item4;
                row.PendingBalance += visit.Item5;
                report.Visits++;
                report.Adults += adults;
                report.Children += children;
                report.Total += visit.Item4;
                report.PendingBalance += visit.Item5;
                if (visit.Item5 > 0m) report.PendingCount++;
                if (visit.Item3.HasValue && visit.Item3.Value <= visit.Item2)
                {
                    var length = (visit.Item2 - visit.Item3.Value).TotalMinutes;
                    minutes.Add(length);
                    List<double> list;
                    if (!dayMinutes.TryGetValue(day, out list)) dayMinutes[day] = list = new List<double>();
                    list.Add(length);
                }
            }
            foreach (var row in days.Values)
            {
                List<double> list;
                if (dayMinutes.TryGetValue(row.Day, out list) && list.Count > 0) row.AverageMinutes = Math.Round(list.Average(), 1);
                report.ByDay.Add(row);
            }
            report.TimedVisits = minutes.Count;
            report.AverageMinutes = minutes.Count == 0 ? (double?)null : Math.Round(minutes.Average(), 1);
            return report;
        }

        // "1 h 25 min" / "40 min" para la pantalla y el CSV.
        public static string FormatDuration(double? minutes)
        {
            if (!minutes.HasValue) return "—";
            var total = (int)Math.Round(minutes.Value, MidpointRounding.AwayFromZero);
            return total >= 60 ? (total / 60) + " h " + (total % 60).ToString("00", CultureInfo.InvariantCulture) + " min" : total + " min";
        }
    }
}
