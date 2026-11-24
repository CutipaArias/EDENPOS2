using System;
using System.Collections.Generic;
using MySqlConnector;

namespace EdenPOS
{
    // Modelos de solo lectura de la pantalla de Reportes (Tasks 3.1 a 3.3).
    public sealed class ShiftReportRow
    {
        public int Number { get; set; }
        public string Employee { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public decimal OpeningAmount { get; set; }
        public decimal? ExpectedAmount { get; set; }
        public decimal? CountedAmount { get; set; }
        public string ClosingNote { get; set; }
        // Contado - esperado; null mientras el turno sigue abierto.
        public decimal? Difference { get { return CountedAmount.HasValue && ExpectedAmount.HasValue ? CountedAmount.Value - ExpectedAmount.Value : (decimal?)null; } }
        // RF-05: Yape esperado y confirmado al cerrar (null en turnos abiertos o cerrados antes de RF-05).
        public decimal? ExpectedYape { get; set; }
        public decimal? CountedYape { get; set; }
        public decimal? YapeDifference { get { return CountedYape.HasValue && ExpectedYape.HasValue ? CountedYape.Value - ExpectedYape.Value : (decimal?)null; } }
    }

    public sealed class ShiftReport
    {
        public List<ShiftReportRow> Rows = new List<ShiftReportRow>();
        public int ClosedCount;
        public decimal OpeningTotal;      // todos los turnos del rango
        public decimal ExpectedTotal;     // solo turnos cerrados
        public decimal CountedTotal;      // solo turnos cerrados
        public decimal DifferenceTotal;   // solo turnos cerrados
        public decimal YapeExpectedTotal;     // solo turnos cerrados con cierre por medio (RF-05)
        public decimal YapeCountedTotal;
        public decimal YapeDifferenceTotal;
    }

    public sealed class PaymentMethodRow
    {
        public string Method { get; set; }
        public int Count { get; set; }
        public decimal Total { get; set; }
        public decimal Cash { get; set; }
        public decimal Yape { get; set; }
    }

    public sealed class CategorySalesRow
    {
        public string Category { get; set; }
        public int Units { get; set; }
        public decimal Amount { get; set; }
    }

    public sealed class PromotionSalesRow
    {
        public string PromotionId { get; set; }
        public int Count { get; set; }
        public decimal Discount { get; set; }
    }

    public sealed class SalesReport
    {
        public int ReceiptCount;
        public decimal TotalSold;
        public decimal AverageTicket { get { return ReceiptCount == 0 ? 0m : Math.Round(TotalSold / ReceiptCount, 2); } }
        public int RefundedCount;
        public decimal RefundedAmount;
        public List<PaymentMethodRow> ByPayment = new List<PaymentMethodRow>();
        public List<CategorySalesRow> ByCategory = new List<CategorySalesRow>();
        public List<PromotionSalesRow> Promotions = new List<PromotionSalesRow>();
    }

    public sealed class InventoryMovementRow
    {
        public string Name { get; set; }
        public int Entries { get; set; }
        public int Sales { get; set; }
        public int Refunds { get; set; }
        // RF-15: unidades dadas de baja por pérdida y por consumo interno.
        public int Losses { get; set; }
        public int InternalUse { get; set; }
        // Ajustes por conteo (con signo: + sobró, - faltó) y cualquier tipo desconocido.
        public int Other { get; set; }
    }

    public sealed class LowStockRow
    {
        public string Name { get; set; }
        public int Stock { get; set; }
        public int Minimum { get; set; }
    }

    public sealed class InventoryReport
    {
        public List<InventoryMovementRow> Movements = new List<InventoryMovementRow>();
        public List<LowStockRow> LowStock = new List<LowStockRow>();
    }

    public static partial class AppData
    {
        public const string UncategorizedLabel = "SIN CATEGORÍA";

        // Consultas de reportes: SELECT agregados de solo lectura. "hasta" incluye todo ese día (rango [desde, hasta+1d)).
        private static void ReportRange(DateTime desde, DateTime hasta, MySqlCommand command)
        {
            command.Parameters.AddWithValue("@desde", desde.Date);
            command.Parameters.AddWithValue("@hasta", hasta.Date.AddDays(1));
        }

        // Turnos cuya apertura cae en el rango. Diferencia = contado - esperado (solo turnos cerrados).
        public static ShiftReport GetShiftReport(DateTime desde, DateTime hasta)
        {
            var report = new ShiftReport();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(
                "SELECT numero, empleado, fecha_apertura, fecha_cierre, monto_inicial, monto_esperado, monto_cierre, observacion_cierre, yape_esperado, yape_contado " +
                "FROM sesion_caja WHERE fecha_apertura >= @desde AND fecha_apertura < @hasta ORDER BY fecha_apertura, numero", connection))
            {
                ReportRange(desde, hasta, command);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var row = new ShiftReportRow
                        {
                            Number = reader.GetInt32(0),
                            Employee = reader.GetString(1),
                            OpenedAt = reader.GetDateTime(2),
                            ClosedAt = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                            OpeningAmount = reader.GetDecimal(4),
                            ExpectedAmount = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5),
                            CountedAmount = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
                            ClosingNote = reader.IsDBNull(7) ? null : reader.GetString(7),
                            ExpectedYape = reader.IsDBNull(8) ? (decimal?)null : reader.GetDecimal(8),
                            CountedYape = reader.IsDBNull(9) ? (decimal?)null : reader.GetDecimal(9)
                        };
                        report.Rows.Add(row);
                        report.OpeningTotal += row.OpeningAmount;
                        if (row.Difference.HasValue)
                        {
                            report.ClosedCount++;
                            report.ExpectedTotal += row.ExpectedAmount.Value;
                            report.CountedTotal += row.CountedAmount.Value;
                            report.DifferenceTotal += row.Difference.Value;
                            if (row.YapeDifference.HasValue)
                            {
                                report.YapeExpectedTotal += row.ExpectedYape.Value;
                                report.YapeCountedTotal += row.CountedYape.Value;
                                report.YapeDifferenceTotal += row.YapeDifference.Value;
                            }
                        }
                    }
            }
            return report;
        }

        // Ventas del rango. Solo recibos 'Pagado' suman al total; los 'Reembolsado' se cuentan aparte (por fecha de venta).
        // Efectivo/Yape por medio de pago siguen la definición de Receipt.CashPaid / YapePaid.
        // Por categoría: importe BRUTO (precio_unitario * cantidad, antes de descuentos).
        public static SalesReport GetSalesReport(DateTime desde, DateTime hasta)
        {
            var report = new SalesReport();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                using (var command = new MySqlCommand(
                    "SELECT medio_pago, COUNT(*), COALESCE(SUM(total), 0), " +
                    "COALESCE(SUM(CASE medio_pago WHEN 'Mixto' THEN monto_efectivo WHEN 'Efectivo' THEN total ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE medio_pago WHEN 'Mixto' THEN monto_yape WHEN 'Yape' THEN total ELSE 0 END), 0) " +
                    "FROM recibo WHERE estado = 'Pagado' AND created_at >= @desde AND created_at < @hasta GROUP BY medio_pago ORDER BY medio_pago", connection))
                {
                    ReportRange(desde, hasta, command);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var row = new PaymentMethodRow
                            {
                                Method = reader.GetString(0), Count = Convert.ToInt32(reader.GetValue(1)), Total = reader.GetDecimal(2),
                                Cash = reader.GetDecimal(3), Yape = reader.GetDecimal(4)
                            };
                            report.ByPayment.Add(row);
                            report.ReceiptCount += row.Count;
                            report.TotalSold += row.Total;
                        }
                }

                using (var command = new MySqlCommand(
                    "SELECT COUNT(*), COALESCE(SUM(total), 0) FROM recibo WHERE estado = 'Reembolsado' AND created_at >= @desde AND created_at < @hasta", connection))
                {
                    ReportRange(desde, hasta, command);
                    using (var reader = command.ExecuteReader())
                        if (reader.Read())
                        {
                            report.RefundedCount = Convert.ToInt32(reader.GetValue(0));
                            report.RefundedAmount = reader.GetDecimal(1);
                        }
                }

                using (var command = new MySqlCommand(
                    "SELECT COALESCE(p.categoria, pv.categoria, @sinCategoria) AS cat, COALESCE(SUM(l.cantidad), 0), COALESCE(SUM(l.precio_unitario * l.cantidad), 0) " +
                    "FROM recibo r JOIN recibo_linea l ON l.id_recibo = r.id_recibo " +
                    "LEFT JOIN producto p ON p.id_producto = l.id_producto " +
                    "LEFT JOIN producto_variante v ON v.id_variante = l.id_variante " +
                    "LEFT JOIN producto pv ON pv.id_producto = v.id_producto " +
                    "WHERE r.estado = 'Pagado' AND r.created_at >= @desde AND r.created_at < @hasta " +
                    "GROUP BY cat ORDER BY 3 DESC, 1", connection))
                {
                    ReportRange(desde, hasta, command);
                    command.Parameters.AddWithValue("@sinCategoria", UncategorizedLabel);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            report.ByCategory.Add(new CategorySalesRow
                            {
                                Category = reader.GetString(0), Units = Convert.ToInt32(reader.GetValue(1)), Amount = reader.GetDecimal(2)
                            });
                }

                using (var command = new MySqlCommand(
                    "SELECT id_promocion_aplicada, COUNT(*), COALESCE(SUM(descuento_promocion), 0) FROM recibo " +
                    "WHERE estado = 'Pagado' AND id_promocion_aplicada IS NOT NULL AND id_promocion_aplicada <> '' " +
                    "AND created_at >= @desde AND created_at < @hasta GROUP BY id_promocion_aplicada ORDER BY 3 DESC, 1", connection))
                {
                    ReportRange(desde, hasta, command);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            report.Promotions.Add(new PromotionSalesRow
                            {
                                PromotionId = reader.GetString(0), Count = Convert.ToInt32(reader.GetValue(1)), Discount = reader.GetDecimal(2)
                            });
                }
            }
            return report;
        }

        // Movimientos del rango por producto/variante (Entrada, Venta, Reembolso, Pérdida, Consumo interno; Ajuste y cualquier otro van a "Other")
        // y productos/variantes con control de inventario en o bajo su stock mínimo (> 0).
        public static InventoryReport GetInventoryReport(DateTime desde, DateTime hasta)
        {
            var report = new InventoryReport();
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                using (var command = new MySqlCommand(
                    "SELECT MAX(producto_nombre), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo = @entrada THEN cantidad ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo = @venta THEN cantidad ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo = @reembolso THEN cantidad ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo = @perdida THEN cantidad ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo = @consumo THEN cantidad ELSE 0 END), 0), " +
                    "COALESCE(SUM(CASE WHEN BINARY tipo NOT IN (@entrada, @venta, @reembolso, @perdida, @consumo) THEN cantidad ELSE 0 END), 0) " +
                    "FROM movimiento_inventario WHERE created_at >= @desde AND created_at < @hasta " +
                    "GROUP BY COALESCE(id_variante, id_producto) ORDER BY 1", connection))
                {
                    ReportRange(desde, hasta, command);
                    command.Parameters.AddWithValue("@entrada", InventoryMovementTypes.Entrada);
                    command.Parameters.AddWithValue("@venta", InventoryMovementTypes.Venta);
                    command.Parameters.AddWithValue("@reembolso", InventoryMovementTypes.Reembolso);
                    command.Parameters.AddWithValue("@perdida", InventoryMovementTypes.Perdida);
                    command.Parameters.AddWithValue("@consumo", InventoryMovementTypes.ConsumoInterno);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            report.Movements.Add(new InventoryMovementRow
                            {
                                Name = reader.GetString(0), Entries = Convert.ToInt32(reader.GetValue(1)), Sales = Convert.ToInt32(reader.GetValue(2)),
                                Refunds = Convert.ToInt32(reader.GetValue(3)), Losses = Convert.ToInt32(reader.GetValue(4)),
                                InternalUse = Convert.ToInt32(reader.GetValue(5)), Other = Convert.ToInt32(reader.GetValue(6))
                            });
                }

                using (var command = new MySqlCommand(
                    "SELECT p.nombre, p.stock, p.stock_minimo FROM producto p " +
                    "WHERE p.activo = 1 AND p.controla_inventario = 1 AND p.stock_minimo > 0 AND p.stock <= p.stock_minimo " +
                    "UNION ALL " +
                    "SELECT CONCAT(p.nombre, ' · ', v.nombre), v.stock, v.stock_minimo FROM producto_variante v JOIN producto p ON p.id_producto = v.id_producto " +
                    "WHERE p.activo = 1 AND v.controla_inventario = 1 AND v.stock_minimo > 0 AND v.stock <= v.stock_minimo " +
                    "ORDER BY 2, 1", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        report.LowStock.Add(new LowStockRow { Name = reader.GetString(0), Stock = reader.GetInt32(1), Minimum = reader.GetInt32(2) });
            }
            return report;
        }
    }
}
