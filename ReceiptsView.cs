using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class ReceiptsView : UserControl, IRefreshable
    {
        private readonly TextBox search;
        private readonly DataGridView receiptGrid;
        private readonly Panel detailHost;
        private readonly Panel listHost;
        private readonly ScreenHeader header;
        private readonly Button paymentSummaryButton;
        private EmptyState listEmpty;
        private Receipt selected;

        public ReceiptsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Canvas;

            header = new ScreenHeader { Title = "Recibos", Subtitle = "Ventas cobradas, agrupadas por día" };
            paymentSummaryButton = Theme.Button("Resumen de cobros", ButtonVariant.Secondary);
            paymentSummaryButton.Click += PaymentSummary_Click;
            header.AddAction(paymentSummaryButton);
            search = Theme.TextBox("Buscar por número, DNI, cliente o pago");
            search.TextChanged += (s, e) => LoadReceipts();
            var searchField = Theme.Field(search, Theme.Dim.Field);
            searchField.Width = 320;
            header.AddAction(searchField);
            searchField.Margin = new Padding(Theme.Space.Sm, (Theme.Dim.Control - Theme.Dim.Field) / 2, 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas,
                Padding = new Padding(Theme.Space.Xxl, Theme.Space.Xl, Theme.Space.Xxl, Theme.Space.Xxl)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(content);
            Controls.Add(header);
            content.BringToFront();

            listHost = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Sm), Margin = new Padding(0, 0, Theme.Space.Xl, 0) };
            receiptGrid = new DataGridView { Dock = DockStyle.Fill };
            Theme.StyleGrid(receiptGrid);
            receiptGrid.ColumnHeadersVisible = false;
            receiptGrid.RowTemplate.Height = 62;
            receiptGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Info", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = { WrapMode = DataGridViewTriState.True } });
            receiptGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", Width = 112, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            receiptGrid.SelectionChanged += (s, e) => SelectCurrent();
            listHost.Controls.Add(receiptGrid);
            content.Controls.Add(listHost, 0, 0);

            detailHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(0), AutoScroll = true, Margin = new Padding(0) };
            content.Controls.Add(detailHost, 1, 0);
            RefreshView();
            AppData.ComprobantesChanged += OnComprobantesChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) AppData.ComprobantesChanged -= OnComprobantesChanged;
            base.Dispose(disposing);
        }

        // Puede llegar desde un hilo de fondo (envío de comprobantes): se pasa al hilo de la interfaz.
        private void OnComprobantesChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) RefreshView(); })); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        public void RefreshView()
        {
            LoadReceipts();
        }

        private void LoadReceipts()
        {
            try
            {
                LoadReceiptsCore();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Recibos (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Recibos");
            }
        }

        private void LoadReceiptsCore()
        {
            if (receiptGrid == null) return;
            UpdatePaymentTotals();
            var previousId = selected == null ? null : selected.Id;
            var term = (search.Text ?? "").Trim();
            var receipts = AppData.Receipts.AsEnumerable();
            if (term.Length > 0)
            {
                receipts = receipts.Where(x =>
                    x.Number.ToString().Contains(term) ||
                    (x.CustomerDni ?? "").Contains(term) ||
                    (x.CustomerName ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    (x.PaymentMethod ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0);
            }

            receiptGrid.Rows.Clear();
            var culture = CultureInfo.GetCultureInfo("es-PE");
            foreach (var day in receipts.GroupBy(x => x.CreatedAt.Date).OrderByDescending(g => g.Key))
            {
                var dayName = day.Key.ToString("dddd dd/MM/yyyy", culture);
                dayName = char.ToUpper(dayName[0], culture) + dayName.Substring(1);
                var dayTotal = day.Where(x => x.Status != "Reembolsado").Sum(x => x.Total);
                var headerIndex = receiptGrid.Rows.Add(dayName, Theme.Money(dayTotal));
                var headerRow = receiptGrid.Rows[headerIndex];
                headerRow.Tag = null; headerRow.Height = 38;
                var style = headerRow.DefaultCellStyle;
                style.BackColor = Theme.SurfaceSunken; style.ForeColor = Theme.Ink; style.Font = Theme.FieldLabel;
                style.SelectionBackColor = Theme.SurfaceSunken; style.SelectionForeColor = Theme.Ink;
                foreach (var receipt in day.OrderByDescending(x => x.CreatedAt))
                {
                    var info = Theme.Money(receipt.Total) + "\n" + receipt.CreatedAt.ToString("HH:mm") + "  ·  " + receipt.PaymentMethod + (receipt.Status == "Reembolsado" ? "  ·  Reembolsado" : "");
                    var index = receiptGrid.Rows.Add(info, "#2-" + receipt.Number);
                    receiptGrid.Rows[index].Tag = receipt;
                    if (receipt.Status == "Reembolsado")
                        receiptGrid.Rows[index].DefaultCellStyle.ForeColor = Theme.Muted;
                    if (receipt.Id == previousId) receiptGrid.Rows[index].Selected = true;
                }
            }

            var firstReceiptRow = receiptGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => x.Tag is Receipt);
            if (firstReceiptRow != null && !receiptGrid.SelectedRows.Cast<DataGridViewRow>().Any(x => x.Tag is Receipt))
            { firstReceiptRow.Selected = true; receiptGrid.CurrentCell = firstReceiptRow.Cells[0]; }
            if (firstReceiptRow != null) SelectCurrent();

            var none = receiptGrid.Rows.Count == 0;
            receiptGrid.Visible = !none;
            if (none && listEmpty == null)
            {
                listEmpty = new EmptyState();
                listHost.Controls.Add(listEmpty);
            }
            if (listEmpty != null)
            {
                listEmpty.Visible = none;
                if (term.Length > 0) { listEmpty.Title = "Sin resultados"; listEmpty.Hint = "Pruebe con otro número, DNI o nombre."; }
                else { listEmpty.Title = "Todavía no hay recibos"; listEmpty.Hint = "Cuando cobre una venta, aparecerá aquí."; }
            }
            if (none)
            {
                selected = null;
                ShowEmpty("Sin recibo seleccionado", "Elija un recibo de la lista para ver su detalle.");
            }
        }

        private void SelectCurrent()
        {
            if (receiptGrid.SelectedRows.Count == 0) return;
            var candidate = receiptGrid.SelectedRows[0].Tag as Receipt;
            if (candidate == null) return;
            selected = candidate;
            RenderDetail();
        }

        private static TableLayoutPanel NewTable(int columns)
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = columns, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent
            };
            return table;
        }

        private static Label Cell(string text, Font font, Color color, ContentAlignment align)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Dock = DockStyle.Fill, TextAlign = align, Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Xs) };
        }

        // Fila clave / valor de una tabla de dos columnas (valor alineado a la derecha).
        private static void AddPair(TableLayoutPanel table, string key, string value, Font font, Color color, Color keyColor)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(Cell(key, font, keyColor, ContentAlignment.MiddleLeft), 0, row);
            table.Controls.Add(Cell(value, font, color, ContentAlignment.MiddleRight), 1, row);
        }

        private static TableLayoutPanel PairTable()
        {
            var table = NewTable(2);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return table;
        }

        private static Control Gap(int height)
        {
            return new Panel { Dock = DockStyle.Top, Height = height, BackColor = Color.Transparent, Margin = new Padding(0) };
        }

        private static Tone ComprobanteTone(string estado)
        {
            switch (estado)
            {
                case ComprobanteEstados.Aceptado: return Tone.Success;
                case ComprobanteEstados.Pendiente: return Tone.Warning;
                case ComprobanteEstados.Rechazado: return Tone.Danger;
                default: return Tone.Neutral;   // Anulado
            }
        }

        private void RenderDetail()
        {
            detailHost.SuspendLayout();
            foreach (var control in detailHost.Controls.Cast<Control>().ToArray()) control.Dispose();
            detailHost.Controls.Clear();
            if (selected == null) { ShowEmpty("Sin recibo seleccionado", "Elija un recibo de la lista para ver su detalle."); detailHost.ResumeLayout(); return; }

            var refunded = selected.Status != "Pagado";
            var card = new CardPanel { Dock = DockStyle.Top, Elevated = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(Theme.Space.Xl), Margin = new Padding(0) };
            // Los controles Top se acoplan en orden inverso al de alta: se añaden de abajo hacia arriba.
            var stack = new System.Collections.Generic.List<Control>();

            // Cabecera: número, fecha y estado.
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var numberBox = NewTable(1);
            numberBox.Controls.Add(Cell("Recibo #2-" + selected.Number, Theme.Title, Theme.Ink, ContentAlignment.MiddleLeft), 0, 0);
            numberBox.Controls.Add(Cell(selected.CreatedAt.ToString("dd/MM/yyyy HH:mm"), Theme.Body, Theme.Muted, ContentAlignment.MiddleLeft), 0, 1);
            numberBox.Dock = DockStyle.Fill;
            var statePill = Theme.Pill(selected.Status, refunded ? Tone.Warning : Tone.Success, true);
            statePill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            head.Controls.Add(numberBox, 0, 0);
            head.Controls.Add(statePill, 1, 0);
            stack.Add(head);

            // Total.
            stack.Add(Gap(Theme.Space.Lg));
            var totalBox = NewTable(1);
            totalBox.Controls.Add(Cell("Total", Theme.FieldLabel, Theme.Muted, ContentAlignment.MiddleLeft), 0, 0);
            totalBox.Controls.Add(Cell(Theme.Money(selected.Total), Theme.Display, refunded ? Theme.Muted : Theme.Ink, ContentAlignment.MiddleLeft), 0, 1);
            stack.Add(totalBox);
            stack.Add(Gap(Theme.Space.Lg));
            stack.Add(SeparatorLine());
            stack.Add(Gap(Theme.Space.Md));

            // Datos del recibo.
            var meta = PairTable();
            AddPair(meta, "Cliente", selected.CustomerName + (string.IsNullOrWhiteSpace(selected.CustomerDni) ? "" : "  ·  DNI " + selected.CustomerDni), Theme.Body, Theme.Ink, Theme.Muted);
            AddPair(meta, "Empleado", selected.Employee, Theme.Body, Theme.Ink, Theme.Muted);
            AddPair(meta, "Medio de pago", selected.PaymentMethod, Theme.Body, Theme.Ink, Theme.Muted);
            AddPair(meta, "Caja","Recepción", Theme.Body, Theme.Ink, Theme.Muted);
            stack.Add(meta);
            stack.Add(Gap(Theme.Space.Md));
            stack.Add(SeparatorLine());
            stack.Add(Gap(Theme.Space.Md));

            // Líneas.
            var lines = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0), BackColor = Color.Transparent };
            lines.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            lines.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var line in selected.Lines)
            {
                var row = lines.RowCount++;
                lines.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var left = NewTable(1);
                left.Controls.Add(Cell(line.Name, Theme.Body, Theme.Ink, ContentAlignment.MiddleLeft), 0, 0);
                left.Controls.Add(Cell(line.Quantity + " x " + Theme.Money(line.UnitPrice), Theme.Caption, Theme.Muted, ContentAlignment.MiddleLeft), 0, 1);
                left.Dock = DockStyle.Fill;
                lines.Controls.Add(left, 0, row);
                lines.Controls.Add(Cell(Theme.Money(line.LineTotal), Theme.Subhead, Theme.Ink, ContentAlignment.MiddleRight), 1, row);
            }
            stack.Add(lines);
            stack.Add(Gap(Theme.Space.Md));
            stack.Add(SeparatorLine());
            stack.Add(Gap(Theme.Space.Md));

            // Subtotal, promoción, total y desglose de pago.
            var sums = PairTable();
            if (selected.Discount > 0m)
            {
                AddPair(sums, "Subtotal", Theme.Money(selected.Subtotal), Theme.Body, Theme.Ink, Theme.Muted);
                AddPair(sums, "Promoción: " + SaleCompleteDialog.PromotionName(selected.PromotionId), "-" + Theme.Money(selected.Discount), Theme.Body, Theme.Success, Theme.Muted);
            }
            AddPair(sums, "Total", Theme.Money(selected.Total), Theme.Emphasis, Theme.Ink, Theme.Ink);
            if (selected.PaymentMethod == "Mixto")
            {
                AddPair(sums, "Efectivo", Theme.Money(selected.CashPaid), Theme.Body, Theme.Ink, Theme.Muted);
                AddPair(sums, "Yape", Theme.Money(selected.YapePaid), Theme.Body, Theme.Ink, Theme.Muted);
            }
            else AddPair(sums, "Pagado con " + selected.PaymentMethod, Theme.Money(selected.Total), Theme.Body, Theme.Ink, Theme.Muted);
            stack.Add(sums);

            // Boleta simulada.
            if (selected.Comprobante != null)
            {
                var comp = selected.Comprobante;
                stack.Add(Gap(Theme.Space.Md));
                stack.Add(SeparatorLine());
                stack.Add(Gap(Theme.Space.Md));
                var boleta = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
                boleta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                boleta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                boleta.Controls.Add(Cell("Boleta " + comp.Documento + " (simulada)", Theme.Body, Theme.Ink, ContentAlignment.MiddleLeft), 0, 0);
                var estado = comp.Estado + (comp.Estado == ComprobanteEstados.Rechazado && comp.CodigoRespuesta != null ? " (" + comp.CodigoRespuesta + ")" : "");
                var pill = Theme.Pill(estado, ComprobanteTone(comp.Estado));
                pill.Anchor = AnchorStyles.Right;
                boleta.Controls.Add(pill, 1, 0);
                stack.Add(boleta);
            }

            // Impresión en la impresora térmica de esta PC (RF-12 / RNF-07).
            stack.Add(Gap(Theme.Space.Lg));
            var printRow = new Panel { Dock = DockStyle.Top, Height = Theme.Dim.Control, BackColor = Color.Transparent };
            var print = Theme.Button("Imprimir", ButtonVariant.Primary);
            print.Width = 140;
            print.Dock = DockStyle.Right;
            print.Click += (s, e) => PrintSelected(false);
            var previewButton = Theme.Button("Vista previa", ButtonVariant.Secondary);
            previewButton.Width = 150;
            previewButton.Dock = DockStyle.Right;
            previewButton.Click += (s, e) => PrintSelected(true);
            var printGap = new Panel { Dock = DockStyle.Right, Width = Theme.Space.Sm, BackColor = Color.Transparent };
            printRow.Controls.Add(previewButton);
            printRow.Controls.Add(printGap);
            printRow.Controls.Add(print);
            stack.Add(printRow);

            // Reembolso.
            stack.Add(Gap(Theme.Space.Lg));
            if (!refunded && !AppSession.IsOwner)
            {
                // RN-12: los trabajadores ven el recibo pero no pueden reembolsarlo.
                var note = Theme.Banner("Solo la dueña puede reembolsar este recibo.", Tone.Neutral);
                note.Dock = DockStyle.Top;
                stack.Add(note);
            }
            else if (!refunded)
            {
                var refund = Theme.Button("Reembolsar", ButtonVariant.Destructive);
                refund.Width = 160;
                refund.Click += Refund_Click;
                var refundRow = new Panel { Dock = DockStyle.Top, Height = Theme.Dim.Control, BackColor = Color.Transparent };
                refund.Dock = DockStyle.Right;
                refundRow.Controls.Add(refund);
                stack.Add(refundRow);
            }
            else
            {
                var banner = Theme.Banner("Recibo reembolsado" + (selected.RefundedAt.HasValue ? " el " + selected.RefundedAt.Value.ToString("dd/MM/yyyy HH:mm") : "") + ". Motivo: " + (selected.RefundReason ?? "Sin motivo") + ".", Tone.Warning);
                banner.Dock = DockStyle.Top;
                stack.Add(banner);
            }

            for (var i = stack.Count - 1; i >= 0; i--) card.Controls.Add(stack[i]);
            detailHost.Controls.Add(card);
            detailHost.ResumeLayout();
        }

        private static Control SeparatorLine()
        {
            var line = Theme.Separator();
            line.Dock = DockStyle.Top;
            return line;
        }

        private void PrintSelected(bool preview)
        {
            if (selected == null) return;
            ReceiptPrinting.PrintReceipt(FindForm(), selected, preview);
            // El estado de la boleta pudo cambiar (se relee antes de imprimir): se actualiza el detalle.
            // Se difiere: RenderDetail desecha el botón que está atendiendo este clic.
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) RenderDetail(); }));
        }

        private void Refund_Click(object sender, EventArgs e)
        {
            string reason;
            if (!PromptDialog.AskText(FindForm(), "Reembolsar recibo", "Motivo del reembolso", out reason)) return;
            if (!Validation.IsValidRequiredText(reason, 3, 160))
            {
                Theme.Message(FindForm(), Tone.Warning, "Ingrese un motivo de 3 a 160 caracteres.", "Validación del reembolso");
                return;
            }
            var confirm = MessageBox.Show(
                "Se marcará el recibo #2-" + selected.Number + " como reembolsado por " + Theme.Money(selected.Total) + ".",
                "Confirmar reembolso",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            try
            {
                AppData.RefundReceipt(selected, reason);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo reembolsar");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Reembolsar recibo");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo reembolsar");
            }
            LoadReceipts();
        }

        private void PaymentSummary_Click(object sender, EventArgs e)
        {
            try
            {
                using (var dialog = new PaymentDateSummaryDialog(DateTime.Today)) dialog.ShowDialog(FindForm());
                UpdatePaymentTotals();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Resumen de pagos (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Recibos");
            }
        }

        private void ShowEmpty(string title, string hint)
        {
            foreach (var control in detailHost.Controls.Cast<Control>().ToArray()) control.Dispose();
            detailHost.Controls.Clear();
            detailHost.Controls.Add(Theme.EmptyState(title, hint));
        }

        private void UpdatePaymentTotals()
        {
            var paidToday = AppData.Receipts.Where(x => x.Status == "Pagado" && x.CreatedAt.Date == DateTime.Today).ToList();
            header.Subtitle = "Hoy: efectivo " + Theme.Money(paidToday.Sum(x => x.CashPaid)) + "  ·  Yape " + Theme.Money(paidToday.Sum(x => x.YapePaid));
        }
    }
}
