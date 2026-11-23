using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MySqlConnector;

namespace EdenPOS
{
    // Piezas comunes de Reportes y Auditoría (solo dueña): selector de rango, bloques con grid y exportación CSV.
    internal static class ReportUi
    {
        private static readonly Font BoldBody = Theme.Emphasis;

        public static Font BoldBodyFont { get { return BoldBody; } }

        // Nombre legible de la promoción (el id se guarda como código en recibo.id_promocion_aplicada).
        public static string PromotionName(string id)
        {
            if (id == SextaVisitaGratis.Codigo) return "6ª visita gratis";
            if (id == DescuentoGrupal.Codigo) return "Descuento grupal";
            return id;
        }

        public static DateTime WeekStart(DateTime day)
        {
            var offset = ((int)day.DayOfWeek + 6) % 7; // lunes = 0
            return day.Date.AddDays(-offset);
        }

        public static DateTimePicker Picker(DateTime value)
        {
            return new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Font = Theme.Body,
                Width = 136,
                Value = value.Date,
                Margin = new Padding(0)
            };
        }

        // Etiqueta (Caption) + campo en una sola pieza de la altura de un botón, para alinearla con los botones de la cabecera.
        public static Control Labeled(string text, Control field)
        {
            var size = TextRenderer.MeasureText(text, Theme.Caption, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var panel = new Panel { Width = size.Width + Theme.Space.Sm + field.Width, Height = Theme.Dim.Control, Margin = new Padding(0) };
            var label = Theme.Label(text);
            label.Font = Theme.Caption;
            label.ForeColor = Theme.Muted;
            label.Location = new Point(0, (panel.Height - size.Height) / 2);
            field.Location = new Point(size.Width + Theme.Space.Sm, Math.Max(0, (panel.Height - field.Height) / 2));
            panel.Controls.Add(label);
            panel.Controls.Add(field);
            return panel;
        }

        // Atajo de rango (Hoy / Esta semana / Este mes): acción terciaria.
        public static EdenButton Shortcut(string text, Action action)
        {
            var button = Theme.Button(text, ButtonVariant.Ghost);
            button.Click += (s, e) => action();
            return button;
        }

        public static DataGridView Grid()
        {
            var grid = new DataGridView { Dock = DockStyle.Fill };
            Theme.StyleGrid(grid);
            grid.AllowUserToResizeColumns = true;
            return grid;
        }

        public static void Column(DataGridView grid, string header, bool right = false, float weight = 1f)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight * 100f, MinimumWidth = 60, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (right)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            grid.Columns.Add(column);
        }

        // Estado vacío del sistema, con el mismo fondo que la tarjeta que lo contiene; se alterna con Show.
        public static EmptyState Empty(string title, string hint)
        {
            var empty = Theme.EmptyState(title, hint);
            empty.BackColor = Theme.Surface;
            empty.Visible = false;
            return empty;
        }

        // Tarjeta blanca con título, nota opcional, grid y estado vacío (se alternan con Show).
        public static Panel Block(string title, string caption, DataGridView grid, Control empty)
        {
            // Margen derecho/inferior = Lg entre tarjetas; el relleno del cuerpo de la pantalla descuenta el de la última.
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Space.Lg, Theme.Space.Lg), Padding = new Padding(1, 1, 1, Theme.Space.Sm) };
            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Surface,
                Padding = new Padding(Theme.Space.Lg, Theme.Space.Md, Theme.Space.Lg, Theme.Space.Sm)
            };
            var titleLabel = Theme.Label(title);
            titleLabel.Font = Theme.Title;
            header.Controls.Add(titleLabel);
            if (!string.IsNullOrEmpty(caption))
            {
                var note = Theme.Label(caption);
                note.Font = Theme.Caption;
                note.ForeColor = Theme.Muted;
                note.Margin = new Padding(0, Theme.Space.Xs, 0, 0);
                header.Controls.Add(note);
            }
            card.Controls.Add(grid);
            card.Controls.Add(empty);
            card.Controls.Add(header);
            return card;
        }

        public static void Show(DataGridView grid, Control empty, bool hasRows)
        {
            grid.Visible = hasRows;
            empty.Visible = !hasRows;
        }

        public static string Fmt(decimal value)
        {
            return value.ToString("0.00", CultureInfo.CurrentCulture);
        }

        // CSV para Excel en español: UTF-8 con BOM y separador ';'. Texto que empiece con = + - @ se neutraliza (inyección de fórmulas).
        public static string BuildCsv(List<object[]> rows)
        {
            var text = new StringBuilder();
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    if (i > 0) text.Append(';');
                    text.Append(Cell(row[i]));
                }
                text.Append("\r\n");
            }
            return text.ToString();
        }

        private static string Cell(object value)
        {
            if (value == null) return "";
            if (value is decimal) return ((decimal)value).ToString("0.00", CultureInfo.CurrentCulture);
            if (value is int) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var s = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            if (s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static void SaveCsv(IWin32Window owner, string defaultName, List<object[]> rows)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Exportar CSV";
                dialog.Filter = "Archivo CSV (*.csv)|*.csv";
                dialog.FileName = defaultName;
                dialog.OverwritePrompt = true;
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, BuildCsv(rows), new UTF8Encoding(true));
                    Theme.Message(owner, Tone.Success, "Se guardó el archivo:\n" + dialog.FileName, "Exportar CSV");
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    Theme.Message(owner, Tone.Warning, "No se pudo guardar el archivo. Verifique que no esté abierto en otro programa y que la carpeta permita escribir.", "Exportar CSV");
                }
            }
        }
    }

    public sealed class ReportsView : UserControl, IRefreshable
    {
        private readonly DateTimePicker fromPicker;
        private readonly DateTimePicker toPicker;
        private readonly ScreenHeader header;
        private readonly Banner messageBanner;
        private readonly Dictionary<string, EdenButton> tabButtons = new Dictionary<string, EdenButton>();
        private readonly Dictionary<string, Control> sections = new Dictionary<string, Control>();
        private string currentSection = "caja";
        private bool suppressReload;

        // Datos cargados (base de la exportación CSV).
        private ShiftReport shiftData;
        private SalesReport salesData;
        private InventoryReport inventoryData;
        private DateTime loadedFrom, loadedTo;

        // Caja
        private Label shiftCountValue, shiftCountDetail, shiftExpectedValue, shiftExpectedDetail, shiftDiffValue;
        private Pill shiftDiffPill;
        private readonly DataGridView shiftGrid = ReportUi.Grid();
        private readonly EmptyState shiftEmpty = ReportUi.Empty("Sin turnos en este rango", "No hubo turnos con apertura en este rango de fechas.");

        // Ventas
        private Label soldValue, soldDetail, receiptsValue, receiptsDetail, ticketValue, ticketDetail, refundValue, refundDetail;
        private readonly DataGridView paymentGrid = ReportUi.Grid();
        private readonly EmptyState paymentEmpty = ReportUi.Empty("Sin ventas en este rango", "No hubo ventas pagadas en este rango de fechas.");
        private readonly DataGridView categoryGrid = ReportUi.Grid();
        private readonly EmptyState categoryEmpty = ReportUi.Empty("Sin ventas en este rango", "No hubo ventas pagadas en este rango de fechas.");
        private readonly DataGridView promoGrid = ReportUi.Grid();
        private readonly EmptyState promoEmpty = ReportUi.Empty("Sin promociones", "Ninguna promoción se aplicó en este periodo.");

        // Inventario
        private Label movedValue, movedDetail, entriesValue, entriesDetail, soldUnitsValue, soldUnitsDetail, lowValue;
        private Pill lowPill;
        private readonly DataGridView movementGrid = ReportUi.Grid();
        private readonly EmptyState movementEmpty = ReportUi.Empty("Sin movimientos", "No hubo movimientos de inventario en este rango de fechas.");
        private readonly DataGridView lowStockGrid = ReportUi.Grid();
        private readonly EmptyState lowStockEmpty = ReportUi.Empty("Todo en orden", "Ningún producto con control de inventario está en su stock mínimo.");

        public ReportsView()
        {
            BackColor = Theme.Canvas;
            Font = Theme.Body;
            Theme.EnableDoubleBuffer(this);

            var today = DateTime.Today;
            fromPicker = ReportUi.Picker(new DateTime(today.Year, today.Month, 1));
            toPicker = ReportUi.Picker(today);
            fromPicker.ValueChanged += (s, e) => Reload();
            toPicker.ValueChanged += (s, e) => Reload();

            // Cabecera: título + rango, y en la zona de acciones fechas, atajos y exportación (el primero añadido queda a la derecha).
            header = new ScreenHeader { Title = "Reportes" };
            var export = Theme.Button("Exportar CSV", ButtonVariant.Secondary);
            export.Click += (s, e) => ExportCurrent();
            header.AddAction(export);
            AddShortcut("Este mes", () => SetRange(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today));
            AddShortcut("Esta semana", () => SetRange(ReportUi.WeekStart(DateTime.Today), DateTime.Today));
            AddShortcut("Hoy", () => SetRange(DateTime.Today, DateTime.Today));
            header.AddAction(ReportUi.Labeled("Hasta", Theme.Field(toPicker)));
            header.AddAction(ReportUi.Labeled("Desde", Theme.Field(fromPicker)));

            // Cuerpo: selector de sección, aviso de error y contenido.
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Lg, Theme.Space.Xxl - Theme.Space.Lg, Theme.Space.Md) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Canvas };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            body.Controls.Add(root);
            Controls.Add(body);
            Controls.Add(header);

            // Selector de sección: segmentos dentro de una cápsula hundida; el activo es blanco con texto verde profundo.
            var segments = new CardPanel
            {
                BackColor = Theme.SurfaceSunken,
                Radius = Theme.Radius.Card,
                Padding = new Padding(Theme.Space.Xs),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 0, Theme.Space.Lg)
            };
            var segmentFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.SurfaceSunken, Margin = new Padding(0) };
            AddTab(segmentFlow, "caja", "Caja");
            AddTab(segmentFlow, "ventas", "Ventas");
            AddTab(segmentFlow, "inventario", "Inventario");
            segments.Controls.Add(segmentFlow);
            root.Controls.Add(segments, 0, 0);

            messageBanner = Theme.Banner("", Tone.Danger);
            messageBanner.Dock = DockStyle.Fill;
            messageBanner.Margin = new Padding(0, 0, Theme.Space.Lg, Theme.Space.Md);
            messageBanner.Visible = false;
            root.Controls.Add(messageBanner, 0, 1);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Margin = new Padding(0) };
            root.Controls.Add(content, 0, 2);

            sections["caja"] = BuildShiftSection();
            sections["ventas"] = BuildSalesSection();
            sections["inventario"] = BuildInventorySection();
            foreach (var section in sections.Values)
            {
                section.Dock = DockStyle.Fill;
                content.Controls.Add(section);
            }
            SelectSection("caja");
            Reload();
        }

        private void AddShortcut(string text, Action action)
        {
            var button = ReportUi.Shortcut(text, action);
            header.AddAction(button);
            button.Width = button.GetPreferredSize(Size.Empty).Width;   // AddAction fija un mínimo de 120; el atajo es corto.
        }

        private void AddTab(FlowLayoutPanel parent, string key, string text)
        {
            var button = Theme.Button(text, ButtonVariant.Ghost);
            button.Width = 128;
            button.Height = Theme.Dim.Field;
            button.Margin = new Padding(Theme.Space.Xs, Theme.Space.Xs, Theme.Space.Xs, Theme.Space.Xs);
            button.Click += (s, e) => SelectSection(key);
            tabButtons[key] = button;
            parent.Controls.Add(button);
        }

        private void SelectSection(string key)
        {
            currentSection = key;
            foreach (var item in tabButtons)
            {
                var active = item.Key == key;
                var button = item.Value;
                button.BackColor = active ? Theme.Surface : Color.Empty;
                button.ForeColor = active ? Theme.GreenDark : Theme.Muted;
                button.FlatAppearance.BorderColor = active ? Theme.BorderStrong : Color.Empty;
                button.FlatAppearance.MouseOverBackColor = active ? Color.Empty : Theme.Divider;
            }
            foreach (var section in sections)
                section.Value.Visible = section.Key == key;
        }

        private void SetRange(DateTime from, DateTime to)
        {
            suppressReload = true;
            fromPicker.Value = from.Date;
            toPicker.Value = to.Date;
            suppressReload = false;
            Reload();
        }

        public void RefreshView()
        {
            Reload();
        }

        private void ShowMessage(string text)
        {
            messageBanner.Text = text ?? "";
            messageBanner.Visible = !string.IsNullOrEmpty(text);
        }

        private void Reload()
        {
            if (suppressReload) return;
            var from = fromPicker.Value.Date;
            var to = toPicker.Value.Date;
            if (from > to)
            {
                ShowMessage("La fecha \"Desde\" no puede ser posterior a \"Hasta\". Ajuste el rango para ver los reportes.");
                return;
            }
            try
            {
                shiftData = AppData.GetShiftReport(from, to);
                salesData = AppData.GetSalesReport(from, to);
                inventoryData = AppData.GetInventoryReport(from, to);
                loadedFrom = from;
                loadedTo = to;
            }
            catch (MySqlException ex)
            {
                AppData.LogError(ex, "Reportes");
                ShowMessage(AppData.DescribeDatabaseError(ex));
                return;
            }
            ShowMessage(null);
            header.Subtitle = from == to
                ? "Resumen del " + from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : "Resumen del " + from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " al " + to.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            BindShifts();
            BindSales();
            BindInventory();
        }

        // ---------- Caja ----------

        private Control BuildShiftSection()
        {
            var layout = SectionLayout(1, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var cards = CardRow(3);
            cards.Controls.Add(StatCard("Turnos en el rango", out shiftCountValue, out shiftCountDetail), 0, 0);
            cards.Controls.Add(StatCard("Esperado en caja (turnos cerrados)", out shiftExpectedValue, out shiftExpectedDetail), 1, 0);
            Label unusedDetail;
            cards.Controls.Add(StatCard("Diferencia total (contado - esperado)", out shiftDiffValue, out unusedDetail, out shiftDiffPill), 2, 0);
            layout.Controls.Add(cards, 0, 0);

            ReportUi.Column(shiftGrid, "N.º", true, 0.5f);
            ReportUi.Column(shiftGrid, "Empleado", false, 1.2f);
            ReportUi.Column(shiftGrid, "Apertura", false, 1.3f);
            ReportUi.Column(shiftGrid, "Cierre", false, 1.3f);
            ReportUi.Column(shiftGrid, "Monto inicial", true);
            ReportUi.Column(shiftGrid, "Esperado", true);
            ReportUi.Column(shiftGrid, "Contado", true);
            ReportUi.Column(shiftGrid, "Diferencia", true);
            ReportUi.Column(shiftGrid, "Observación", false, 1.6f);
            layout.Controls.Add(ReportUi.Block("Turnos de caja", "Turnos cuya apertura cae en el rango. Un faltante se resalta en rojo y un sobrante en ámbar.", shiftGrid, shiftEmpty), 0, 1);
            return layout;
        }

        // Faltante (negativo) = Danger; sobrante (positivo) = Warning.
        private static Color DifferenceColor(decimal difference)
        {
            return difference < 0m ? Theme.Danger : Theme.Warning;
        }

        private void BindShifts()
        {
            var report = shiftData;
            shiftGrid.Rows.Clear();
            foreach (var row in report.Rows)
            {
                var index = shiftGrid.Rows.Add(
                    row.Number.ToString(CultureInfo.InvariantCulture), row.Employee,
                    row.OpenedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    row.ClosedAt.HasValue ? row.ClosedAt.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "Abierto",
                    Theme.Money(row.OpeningAmount),
                    row.ExpectedAmount.HasValue ? Theme.Money(row.ExpectedAmount.Value) : "—",
                    row.CountedAmount.HasValue ? Theme.Money(row.CountedAmount.Value) : "—",
                    row.Difference.HasValue ? Theme.Money(row.Difference.Value) : "—",
                    row.ClosingNote ?? "");
                if (!row.ClosedAt.HasValue) shiftGrid.Rows[index].Cells[3].Style.ForeColor = Theme.Muted;
                if (row.Difference.HasValue && row.Difference.Value != 0m)
                {
                    var style = shiftGrid.Rows[index].Cells[7].Style;
                    style.ForeColor = DifferenceColor(row.Difference.Value);
                    style.Font = ReportUi.BoldBodyFont;
                }
            }
            if (report.Rows.Count > 0)
            {
                var index = shiftGrid.Rows.Add("", "Total", "", "", Theme.Money(report.OpeningTotal), Theme.Money(report.ExpectedTotal), Theme.Money(report.CountedTotal), Theme.Money(report.DifferenceTotal), "");
                var totalRow = shiftGrid.Rows[index];
                totalRow.DefaultCellStyle.Font = ReportUi.BoldBodyFont;
                totalRow.DefaultCellStyle.BackColor = Theme.SurfaceSunken;
                if (report.DifferenceTotal != 0m) totalRow.Cells[7].Style.ForeColor = DifferenceColor(report.DifferenceTotal);
            }
            ReportUi.Show(shiftGrid, shiftEmpty, report.Rows.Count > 0);
            shiftGrid.ClearSelection();

            shiftCountValue.Text = report.Rows.Count.ToString(CultureInfo.InvariantCulture);
            var open = report.Rows.Count - report.ClosedCount;
            shiftCountDetail.Text = report.ClosedCount + (report.ClosedCount == 1 ? " cerrado, " : " cerrados, ") + open + (open == 1 ? " abierto" : " abiertos");
            shiftExpectedValue.Text = Theme.Money(report.ExpectedTotal);
            shiftExpectedDetail.Text = "Contado: " + Theme.Money(report.CountedTotal);
            shiftDiffValue.Text = Theme.Money(report.DifferenceTotal);
            shiftDiffValue.ForeColor = report.DifferenceTotal != 0m ? DifferenceColor(report.DifferenceTotal) : Theme.Ink;
            shiftDiffPill.Text = report.DifferenceTotal == 0m ? "Sin diferencia neta" : report.DifferenceTotal < 0m ? "Faltante en caja" : "Sobrante en caja";
            shiftDiffPill.Tone = report.DifferenceTotal == 0m ? Tone.Success : report.DifferenceTotal < 0m ? Tone.Danger : Tone.Warning;
        }

        // ---------- Ventas ----------

        private Control BuildSalesSection()
        {
            var layout = SectionLayout(2, 3);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            var cards = CardRow(4);
            cards.Controls.Add(StatCard("Total vendido", out soldValue, out soldDetail), 0, 0);
            cards.Controls.Add(StatCard("Recibos pagados", out receiptsValue, out receiptsDetail), 1, 0);
            cards.Controls.Add(StatCard("Ticket promedio", out ticketValue, out ticketDetail), 2, 0);
            cards.Controls.Add(StatCard("Reembolsos", out refundValue, out refundDetail), 3, 0);
            layout.Controls.Add(cards, 0, 0);
            layout.SetColumnSpan(cards, 2);

            ReportUi.Column(paymentGrid, "Medio de pago", false, 1.6f);
            ReportUi.Column(paymentGrid, "Recibos", true, 0.8f);
            ReportUi.Column(paymentGrid, "Total", true);
            ReportUi.Column(paymentGrid, "En efectivo", true);
            ReportUi.Column(paymentGrid, "En Yape", true);
            layout.Controls.Add(ReportUi.Block("Por medio de pago", "En los pagos mixtos, cada parte se suma a efectivo o a Yape.", paymentGrid, paymentEmpty), 0, 1);

            ReportUi.Column(promoGrid, "Promoción", false, 1.4f);
            ReportUi.Column(promoGrid, "Veces aplicada", true);
            ReportUi.Column(promoGrid, "Descuento total", true);
            layout.Controls.Add(ReportUi.Block("Promociones aplicadas", "Recibos pagados con promoción y descuento otorgado.", promoGrid, promoEmpty), 1, 1);

            ReportUi.Column(categoryGrid, "Categoría", false, 1.6f);
            ReportUi.Column(categoryGrid, "Unidades", true);
            ReportUi.Column(categoryGrid, "Importe", true);
            var categoryBlock = ReportUi.Block("Ventas por categoría, antes de descuentos", "Importe bruto, antes de descuentos: la suma puede superar el total vendido.", categoryGrid, categoryEmpty);
            layout.Controls.Add(categoryBlock, 0, 2);
            layout.SetColumnSpan(categoryBlock, 2);
            return layout;
        }

        private void BindSales()
        {
            var report = salesData;
            soldValue.Text = Theme.Money(report.TotalSold);
            soldDetail.Text = "Con descuentos aplicados";
            receiptsValue.Text = report.ReceiptCount.ToString(CultureInfo.InvariantCulture);
            receiptsDetail.Text = report.ReceiptCount == 1 ? "Recibo pagado en el rango" : "Recibos pagados en el rango";
            ticketValue.Text = Theme.Money(report.AverageTicket);
            ticketDetail.Text = "Total vendido / recibos";
            refundValue.Text = report.RefundedCount.ToString(CultureInfo.InvariantCulture);
            refundValue.ForeColor = report.RefundedCount > 0 ? Theme.Warning : Theme.Ink;
            refundDetail.Text = Theme.Money(report.RefundedAmount) + " (no incluido en el total)";

            paymentGrid.Rows.Clear();
            foreach (var row in report.ByPayment)
                paymentGrid.Rows.Add(row.Method, row.Count.ToString(CultureInfo.InvariantCulture), Theme.Money(row.Total), Theme.Money(row.Cash), Theme.Money(row.Yape));
            ReportUi.Show(paymentGrid, paymentEmpty, report.ByPayment.Count > 0);
            paymentGrid.ClearSelection();

            categoryGrid.Rows.Clear();
            foreach (var row in report.ByCategory)
                categoryGrid.Rows.Add(row.Category, row.Units.ToString(CultureInfo.InvariantCulture), Theme.Money(row.Amount));
            ReportUi.Show(categoryGrid, categoryEmpty, report.ByCategory.Count > 0);
            categoryGrid.ClearSelection();

            promoGrid.Rows.Clear();
            foreach (var row in report.Promotions)
                promoGrid.Rows.Add(ReportUi.PromotionName(row.PromotionId), row.Count.ToString(CultureInfo.InvariantCulture), Theme.Money(row.Discount));
            ReportUi.Show(promoGrid, promoEmpty, report.Promotions.Count > 0);
            promoGrid.ClearSelection();
        }

        // ---------- Inventario ----------

        private Control BuildInventorySection()
        {
            var layout = SectionLayout(1, 3);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));

            var cards = CardRow(4);
            cards.Controls.Add(StatCard("Productos con movimiento", out movedValue, out movedDetail), 0, 0);
            cards.Controls.Add(StatCard("Entradas", out entriesValue, out entriesDetail), 1, 0);
            cards.Controls.Add(StatCard("Salidas por venta", out soldUnitsValue, out soldUnitsDetail), 2, 0);
            Label unusedDetail;
            cards.Controls.Add(StatCard("En stock mínimo o por debajo", out lowValue, out unusedDetail, out lowPill), 3, 0);
            layout.Controls.Add(cards, 0, 0);

            ReportUi.Column(movementGrid, "Producto o variante", false, 2.2f);
            ReportUi.Column(movementGrid, "Entradas", true);
            ReportUi.Column(movementGrid, "Salidas por venta", true);
            ReportUi.Column(movementGrid, "Reembolsos (devueltos)", true);
            ReportUi.Column(movementGrid, "Otros ajustes", true);
            layout.Controls.Add(ReportUi.Block("Movimientos del rango", "Unidades por producto o variante, según el tipo de movimiento.", movementGrid, movementEmpty), 0, 1);

            ReportUi.Column(lowStockGrid, "Producto o variante", false, 2.2f);
            ReportUi.Column(lowStockGrid, "Stock actual", true);
            ReportUi.Column(lowStockGrid, "Stock mínimo", true);
            ReportUi.Column(lowStockGrid, "Estado", true);
            layout.Controls.Add(ReportUi.Block("En stock mínimo o por debajo", "Solo productos con control de inventario y stock mínimo mayor que cero. No depende del rango de fechas.", lowStockGrid, lowStockEmpty), 0, 2);
            return layout;
        }

        private void BindInventory()
        {
            var report = inventoryData;
            movementGrid.Rows.Clear();
            var entries = 0; var sold = 0;
            foreach (var row in report.Movements)
            {
                entries += row.Entries; sold += row.Sales;
                movementGrid.Rows.Add(row.Name, row.Entries.ToString(CultureInfo.InvariantCulture), row.Sales.ToString(CultureInfo.InvariantCulture),
                    row.Refunds.ToString(CultureInfo.InvariantCulture), row.Other.ToString(CultureInfo.InvariantCulture));
            }
            ReportUi.Show(movementGrid, movementEmpty, report.Movements.Count > 0);
            movementGrid.ClearSelection();

            lowStockGrid.Rows.Clear();
            foreach (var row in report.LowStock)
            {
                var soldOut = row.Stock <= 0;
                var index = lowStockGrid.Rows.Add(row.Name, row.Stock.ToString(CultureInfo.InvariantCulture), row.Minimum.ToString(CultureInfo.InvariantCulture), soldOut ? "Agotado" : "Stock bajo");
                var color = soldOut ? Theme.Danger : Theme.Warning;
                lowStockGrid.Rows[index].Cells[1].Style.ForeColor = color;
                lowStockGrid.Rows[index].Cells[1].Style.Font = ReportUi.BoldBodyFont;
                lowStockGrid.Rows[index].Cells[3].Style.ForeColor = color;
                lowStockGrid.Rows[index].Cells[3].Style.Font = ReportUi.BoldBodyFont;
            }
            ReportUi.Show(lowStockGrid, lowStockEmpty, report.LowStock.Count > 0);
            lowStockGrid.ClearSelection();

            movedValue.Text = report.Movements.Count.ToString(CultureInfo.InvariantCulture);
            movedDetail.Text = "Productos o variantes en el rango";
            entriesValue.Text = entries.ToString(CultureInfo.InvariantCulture);
            entriesDetail.Text = "Unidades ingresadas";
            soldUnitsValue.Text = sold.ToString(CultureInfo.InvariantCulture);
            soldUnitsDetail.Text = "Unidades descontadas por ventas";
            lowValue.Text = report.LowStock.Count.ToString(CultureInfo.InvariantCulture);
            lowValue.ForeColor = report.LowStock.Count > 0 ? Theme.Danger : Theme.Ink;
            lowPill.Text = report.LowStock.Count == 0 ? "Todo en orden" : "Reponer pronto";
            lowPill.Tone = report.LowStock.Count == 0 ? Tone.Success : Tone.Danger;
        }

        // ---------- Exportación ----------

        private void ExportCurrent()
        {
            if (shiftData == null || salesData == null || inventoryData == null)
            {
                Theme.Message(this, Tone.Warning, "Aún no hay datos cargados. Corrija el rango de fechas e intente de nuevo.", "Exportar CSV");
                return;
            }
            var range = loadedFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + loadedTo.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            ReportUi.SaveCsv(this, "reporte-" + currentSection + "-" + range + ".csv", CsvRows(currentSection));
        }

        // Filas del CSV de la sección indicada (encabezado + datos + totales). Pública para poder probarla sin UI.
        internal List<object[]> CsvRows(string section)
        {
            var rows = new List<object[]>();
            if (section == "caja")
            {
                rows.Add(new object[] { "Número", "Empleado", "Apertura", "Cierre", "Monto inicial", "Esperado", "Contado", "Diferencia", "Observación" });
                foreach (var r in shiftData.Rows)
                    rows.Add(new object[] { r.Number, r.Employee, r.OpenedAt, r.ClosedAt.HasValue ? (object)r.ClosedAt.Value : "Abierto", r.OpeningAmount, r.ExpectedAmount, r.CountedAmount, r.Difference, r.ClosingNote ?? "" });
                rows.Add(new object[] { "", "Total (esperado, contado y diferencia: solo turnos cerrados)", "", "", shiftData.OpeningTotal, shiftData.ExpectedTotal, shiftData.CountedTotal, shiftData.DifferenceTotal, "" });
            }
            else if (section == "ventas")
            {
                rows.Add(new object[] { "Resumen", "Valor" });
                rows.Add(new object[] { "Total vendido (recibos pagados, con descuentos)", salesData.TotalSold });
                rows.Add(new object[] { "Recibos pagados", salesData.ReceiptCount });
                rows.Add(new object[] { "Ticket promedio", salesData.AverageTicket });
                rows.Add(new object[] { "Recibos reembolsados", salesData.RefundedCount });
                rows.Add(new object[] { "Monto reembolsado", salesData.RefundedAmount });
                rows.Add(new object[0]);
                rows.Add(new object[] { "Medio de pago", "Recibos", "Total", "En efectivo", "En Yape" });
                foreach (var r in salesData.ByPayment) rows.Add(new object[] { r.Method, r.Count, r.Total, r.Cash, r.Yape });
                rows.Add(new object[0]);
                rows.Add(new object[] { "Categoría (importe bruto, antes de descuentos)", "Unidades", "Importe" });
                foreach (var r in salesData.ByCategory) rows.Add(new object[] { r.Category, r.Units, r.Amount });
                rows.Add(new object[0]);
                rows.Add(new object[] { "Promoción", "Veces aplicada", "Descuento total" });
                foreach (var r in salesData.Promotions) rows.Add(new object[] { ReportUi.PromotionName(r.PromotionId), r.Count, r.Discount });
            }
            else
            {
                rows.Add(new object[] { "Producto o variante", "Entradas", "Salidas por venta", "Reembolsos (devueltos)", "Otros ajustes" });
                foreach (var r in inventoryData.Movements) rows.Add(new object[] { r.Name, r.Entries, r.Sales, r.Refunds, r.Other });
                rows.Add(new object[0]);
                rows.Add(new object[] { "En stock mínimo o por debajo", "Stock actual", "Stock mínimo" });
                foreach (var r in inventoryData.LowStock) rows.Add(new object[] { r.Name, r.Stock, r.Minimum });
            }
            return rows;
        }

        // ---------- Piezas de diseño ----------

        private static TableLayoutPanel SectionLayout(int columns, int rows)
        {
            var layout = new TableLayoutPanel { ColumnCount = columns, RowCount = rows, BackColor = Theme.Canvas, Margin = new Padding(0) };
            for (var i = 0; i < columns; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            return layout;
        }

        // Fila de tarjetas de resumen: columnas iguales que se reparten el ancho disponible.
        private static TableLayoutPanel CardRow(int count)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = count, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Canvas, Margin = new Padding(0) };
            for (var i = 0; i < count; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return row;
        }

        private static CardPanel StatCard(string title, out Label value, out Label detail)
        {
            Pill unused;
            return StatCard(title, out value, out detail, out unused, false);
        }

        // Con estado (Pill) en lugar del texto de detalle.
        private static CardPanel StatCard(string title, out Label value, out Label detail, out Pill status)
        {
            return StatCard(title, out value, out detail, out status, true);
        }

        // Etiqueta (Caption) arriba, cifra principal (Display) y detalle (Caption) o estado (Pill) debajo.
        private static CardPanel StatCard(string title, out Label value, out Label detail, out Pill status, bool withPill)
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, Theme.Space.Lg, Theme.Space.Lg),
                Padding = new Padding(Theme.Space.Lg, Theme.Space.Md, Theme.Space.Lg, Theme.Space.Md)
            };
            var inner = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            var caption = Theme.Label(title);
            caption.Font = Theme.Caption;
            caption.ForeColor = Theme.Muted;
            value = Theme.Label("—");
            value.Font = Theme.Display;
            detail = Theme.Label("");
            detail.Font = Theme.Caption;
            detail.ForeColor = Theme.Muted;
            status = null;
            inner.Controls.Add(caption);
            inner.Controls.Add(value);
            if (withPill)
            {
                status = Theme.Pill("—", Tone.Neutral);
                status.Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Xs);
                inner.Controls.Add(status);
            }
            else
            {
                detail.Margin = new Padding(0, 0, 0, Theme.Space.Xs);
                inner.Controls.Add(detail);
            }
            card.Controls.Add(inner);
            return card;
        }
    }
}
