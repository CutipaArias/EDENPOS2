using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace EdenPOS
{
    // Utilidades comunes de los diálogos de cobro: misma familia visual (título Title, cuerpo con aire, acciones abajo a la derecha).
    internal static class DialogKit
    {
        public static void Prepare(Form form, string text, int width, int height)
        {
            form.Text = text;
            form.StartPosition = FormStartPosition.CenterParent;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.ClientSize = new Size(width, height);
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.BackColor = Theme.Surface;
            form.Font = Theme.Body;
        }

        // Título (Title) y subtítulo opcional. Dock = Top.
        public static Panel Header(string title, string subtitle = null)
        {
            var hasSubtitle = !string.IsNullOrEmpty(subtitle);
            var panel = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Sm) };
            panel.Height = panel.Padding.Vertical + Theme.Title.Height + (hasSubtitle ? Theme.Body.Height + Theme.Space.Xs : 0) + 4;
            if (hasSubtitle)
                panel.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Top, Height = Theme.Body.Height + Theme.Space.Xs, Font = Theme.Body, ForeColor = Theme.Muted, AutoEllipsis = true });
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = Theme.Title.Height + 4, Font = Theme.Title, ForeColor = Theme.Ink, AutoEllipsis = true });
            return panel;
        }

        // Barra de acciones inferior: la primera de "right" queda a la derecha del todo (acción primaria). "left" opcional (p. ej. destructiva).
        public static Panel Actions(Control left, params Control[] right)
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.Dim.Control + Theme.Space.Md * 2 + 1, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xl, Theme.Space.Md, Theme.Space.Xl, Theme.Space.Md) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            foreach (var button in right)
            {
                var edenButton = button as EdenButton;
                if (edenButton != null) button.Width = Math.Max(120, edenButton.GetPreferredSize(Size.Empty).Width + Theme.Space.Sm);
                button.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
                flow.Controls.Add(button);
            }
            bar.Controls.Add(flow);
            if (left != null)
            {
                left.Dock = DockStyle.Left;
                bar.Controls.Add(left);
            }
            bar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0); };
            return bar;
        }

        public static Panel Body()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Theme.Space.Xl, Theme.Space.Sm, Theme.Space.Xl, Theme.Space.Xl) };
        }

        // Pila vertical de filas autoajustadas.
        public static TableLayoutPanel Stack()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            return layout;
        }

        public static void Row(TableLayoutPanel layout, Control control, int top = 0, int bottom = 0)
        {
            control.Margin = new Padding(0, top, 0, bottom);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control);
        }
    }

    public sealed class CheckoutDialog : Form
    {
        private readonly decimal total;
        private readonly bool checkDni;
        private readonly string customerDni;
        private readonly NumericUpDown received;
        private readonly NumericUpDown yapeAmount;
        private readonly EdenField receivedField;
        private readonly EdenField yapeField;
        private readonly Label receivedLabel;
        private readonly Label yapeLabel;
        private readonly Label changeLabel;
        private readonly Button confirm;
        private readonly SegmentChip[] paymentButtons;

        // Promoción mostrada al cajero. AddReceipt la recalcula y rechaza el cobro si cambió (parámetros expectedDiscount/expectedPromotionId).
        public decimal Discount { get; private set; }
        public string PromotionId { get; private set; }

        public string PaymentMethod { get; private set; } = "Efectivo";
        public decimal AmountReceived { get { return received.Value; } }
        public decimal CashAmount { get { return PaymentMethod == "Mixto" ? received.Value : PaymentMethod == "Efectivo" ? total : 0m; } }
        public decimal YapeAmount { get { return PaymentMethod == "Mixto" ? yapeAmount.Value : PaymentMethod == "Yape" ? total : 0m; } }

        public CheckoutDialog(decimal totalAmount) : this(totalAmount, null)
        {
        }

        // subtotal = suma de las líneas a precio original; el total a cobrar y las validaciones de pago usan el total ya descontado.
        public CheckoutDialog(decimal subtotal, PromocionResultado promotion) : this(subtotal, promotion, null, false)
        {
        }

        // RN-11: con customerDni se valida que ventas por encima de AppData.DniObligatorioDesde (ya con descuento) lleven DNI.
        public CheckoutDialog(decimal subtotal, PromocionResultado promotion, string customerDni) : this(subtotal, promotion, customerDni, true)
        {
        }

        private CheckoutDialog(decimal subtotal, PromocionResultado promotion, string dni, bool validateDni)
        {
            checkDni = validateDni;
            customerDni = Validation.NormalizeDni(dni);
            var hasPromotion = promotion != null && promotion.Aplica;
            Discount = hasPromotion ? promotion.Descuento : 0m;
            PromotionId = hasPromotion ? promotion.PromocionId : null;
            total = subtotal - Discount;
            var needsDni = checkDni && total > AppData.DniObligatorioDesde && customerDni.Length == 0;
            DialogKit.Prepare(this, "Cobrar ticket", 540, 590 + (hasPromotion ? 60 : 0) + (needsDni ? 70 : 0));

            var body = DialogKit.Body();
            var stack = DialogKit.Stack();
            body.Controls.Add(stack);

            // Importe a cobrar como cifra principal
            DialogKit.Row(stack, Theme.LabelField("Total a cobrar"));
            var amount = new Label { Text = Theme.Money(total), Font = Theme.Display, ForeColor = Theme.Ink, AutoSize = true };
            DialogKit.Row(stack, amount, 0, hasPromotion ? 0 : Theme.Space.Md);
            if (hasPromotion)
            {
                DialogKit.Row(stack, Theme.Label("Subtotal " + Theme.Money(subtotal), 10.5f, FontStyle.Regular, Theme.Muted), 0, Theme.Space.Sm);
                var promoBanner = Theme.Banner("Promoción: " + promotion.Descripcion + " (−" + Theme.Money(Discount) + ")", Tone.Success);
                promoBanner.Dock = DockStyle.Fill;
                DialogKit.Row(stack, promoBanner, 0, Theme.Space.Md);
            }
            if (needsDni)
            {
                var dniBanner = Theme.Banner("Las ventas mayores a " + Theme.Money(AppData.DniObligatorioDesde) + " requieren el DNI del cliente. Cancele, ingréselo en el ticket y vuelva a cobrar.", Tone.Warning);
                dniBanner.Dock = DockStyle.Fill;
                DialogKit.Row(stack, dniBanner, 0, Theme.Space.Md);
            }

            // Medio de pago como segmentos
            DialogKit.Row(stack, Theme.LabelField("Medio de pago"), 0, Theme.Space.Sm);
            var segments = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Height = Theme.Dim.Control, Margin = new Padding(0), Padding = new Padding(0) };
            for (var i = 0; i < 3; i++) segments.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            var cash = PaymentButton("Efectivo");
            var yape = PaymentButton("Yape");
            var mixed = PaymentButton("Mixto");
            paymentButtons = new[] { cash, yape, mixed };
            for (var i = 0; i < 3; i++)
            {
                paymentButtons[i].Dock = DockStyle.Fill;
                paymentButtons[i].Margin = new Padding(i == 0 ? 0 : Theme.Space.Sm, 0, 0, 0);
                segments.Controls.Add(paymentButtons[i], i, 0);
            }
            DialogKit.Row(stack, segments, 0, Theme.Space.Lg);

            receivedLabel = Theme.LabelField("Importe recibido en efectivo");
            DialogKit.Row(stack, receivedLabel, 0, Theme.Space.Xs);
            received = new NumericUpDown
            {
                Font = Theme.Emphasis,
                DecimalPlaces = 2,
                Maximum = 1000000,
                Minimum = 0,
                Value = total,
                ThousandsSeparator = true
            };
            received.ValueChanged += (s, e) => UpdateChange();
            receivedField = Theme.Field(received, Theme.Dim.Control);
            receivedField.Dock = DockStyle.Fill;
            DialogKit.Row(stack, receivedField, 0, Theme.Space.Md);

            yapeLabel = Theme.LabelField("Monto pagado por Yape");
            DialogKit.Row(stack, yapeLabel, 0, Theme.Space.Xs);
            yapeAmount = new NumericUpDown
            {
                Font = Theme.Emphasis, DecimalPlaces = 2, Maximum = 1000000, Minimum = 0, Value = 0, ThousandsSeparator = true
            };
            yapeAmount.ValueChanged += (s, e) => UpdateChange();
            yapeField = Theme.Field(yapeAmount, Theme.Dim.Control);
            yapeField.Dock = DockStyle.Fill;
            DialogKit.Row(stack, yapeField, 0, Theme.Space.Md);

            changeLabel = new Label { Font = Theme.Emphasis, AutoSize = true, ForeColor = Theme.Success };
            DialogKit.Row(stack, changeLabel);

            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            confirm = Theme.Button("Confirmar cobro", ButtonVariant.Primary);
            confirm.Click += Confirm_Click;
            var actions = DialogKit.Actions(null, confirm, cancel);
            Controls.Add(body);
            Controls.Add(actions);
            Controls.Add(DialogKit.Header("Cobrar ticket"));
            CancelButton = cancel;
            AcceptButton = confirm;
            SelectPayment(cash);
            UpdateChange();
            Theme.FitDialogToWorkingArea(this);
        }

        private SegmentChip PaymentButton(string text)
        {
            var chip = new SegmentChip(text) { Tag = text, ShowCheck = true, CornerRadius = Theme.Radius.Control, Height = Theme.Dim.Control };
            chip.Click += (s, e) => SelectPayment((SegmentChip)s);
            return chip;
        }

        private void SelectPayment(SegmentChip selected)
        {
            PaymentMethod = selected.Tag.ToString();
            foreach (var chip in paymentButtons ?? new SegmentChip[0]) chip.Active = chip == selected;
            var cash = PaymentMethod == "Efectivo";
            var mixed = PaymentMethod == "Mixto";
            receivedLabel.Text = mixed ? "Monto pagado en efectivo" : "Importe recibido en efectivo";
            receivedLabel.Visible = cash || mixed;
            receivedField.Visible = cash || mixed;
            received.Enabled = cash || mixed;
            yapeLabel.Visible = mixed;
            yapeField.Visible = mixed;
            yapeAmount.Enabled = mixed;
            if (cash) { received.Value = Math.Min(received.Maximum, total); yapeAmount.Value = 0m; }
            else if (mixed) { received.Value = Math.Min(received.Maximum, Math.Round(total / 2m, 2)); yapeAmount.Value = total - received.Value; }
            else { received.Value = Math.Min(received.Maximum, total); yapeAmount.Value = PaymentMethod == "Yape" ? total : 0m; }
            UpdateChange();
        }

        private void UpdateChange()
        {
            if (changeLabel == null) return;
            if (PaymentMethod == "Mixto")
            {
                var difference = total - received.Value - yapeAmount.Value;
                changeLabel.Text = difference == 0m ? "✓  Pago completo: " + Theme.Money(total) : difference > 0m ? "Falta: " + Theme.Money(difference) : "Excede: " + Theme.Money(-difference);
                changeLabel.ForeColor = difference == 0m ? Theme.Success : Theme.Danger;
                if (confirm != null) confirm.Enabled = difference == 0m && received.Value > 0m && yapeAmount.Value > 0m;
            }
            else if (PaymentMethod == "Efectivo")
            {
                var missing = total - received.Value;
                changeLabel.Text = missing > 0m ? "Falta: " + Theme.Money(missing) : "Vuelto: " + Theme.Money(received.Value - total);
                changeLabel.ForeColor = missing > 0m ? Theme.Danger : Theme.Success;
                if (confirm != null) confirm.Enabled = received.Value >= total;
            }
            else
            {
                changeLabel.Text = "Pago por " + PaymentMethod + ": " + Theme.Money(total);
                changeLabel.ForeColor = Theme.Muted;
                if (confirm != null) confirm.Enabled = true;
            }
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            if (checkDni && total > AppData.DniObligatorioDesde && customerDni.Length == 0)
            {
                Theme.Message(this, Tone.Warning, "Las ventas mayores a " + Theme.Money(AppData.DniObligatorioDesde) + " requieren el DNI del cliente. Cancele este cobro, ingrese el DNI en el ticket y vuelva a cobrar.", "DNI obligatorio");
                return;
            }
            if (PaymentMethod == "Efectivo" && received.Value < total)
            {
                Theme.Message(this, Tone.Warning, "El importe recibido es menor que el total.", "Cobro");
                return;
            }
            if (PaymentMethod == "Mixto" && (received.Value <= 0m || yapeAmount.Value <= 0m || received.Value + yapeAmount.Value != total))
            {
                Theme.Message(this, Tone.Warning, "En pago mixto, los montos de efectivo y Yape deben ser mayores que cero y sumar exactamente " + Theme.Money(total) + ".", "Cobro mixto");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // Círculo de "completado": relleno Success con marca blanca.
    internal sealed class CheckBadge : Control
    {
        public CheckBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(72, 72);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Theme.SuccessSoft)) g.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
            using (var brush = new SolidBrush(Theme.Success)) g.FillEllipse(brush, 8, 8, Width - 17, Height - 17);
            using (var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new Point(Width * 3 / 10 + 2, Height / 2 + 1), new Point(Width * 45 / 100, Height * 62 / 100), new Point(Width * 7 / 10, Height * 38 / 100) });
        }
    }

    public sealed class SaleCompleteDialog : Form
    {
        public SaleCompleteDialog(Receipt receipt, decimal change)
        {
            DialogKit.Prepare(this, "Venta completada", 480, 560);

            var body = DialogKit.Body();
            body.Padding = new Padding(Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Md);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
            body.Controls.Add(layout);

            var badge = new CheckBadge { Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, Theme.Space.Md) };
            AddSpan(layout, badge);
            AddSpan(layout, new Label { Text = "Venta completada", Font = Theme.Heading, ForeColor = Theme.Success, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, Theme.Space.Xs) });
            AddSpan(layout, new Label { Text = Theme.Money(receipt.Total), Font = Theme.Display, ForeColor = Theme.Ink, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, Theme.Space.Lg) });

            var rule = Theme.Separator();
            rule.Dock = DockStyle.Fill;
            rule.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            AddSpan(layout, rule);
            AddDetail(layout, "Recibo", "#2-" + receipt.Number, Theme.Ink);
            if (receipt.Comprobante != null) AddDetail(layout, "Comprobante", "Boleta " + receipt.Comprobante.Documento + " (simulada)", Theme.Ink);
            AddDetail(layout, "Medio de pago", receipt.PaymentMethod, Theme.Ink);
            if (receipt.Discount > 0m) AddDetail(layout, PromotionName(receipt.PromotionId), "−" + Theme.Money(receipt.Discount), Theme.Success);
            if (change > 0) AddDetail(layout, "Vuelto", Theme.Money(change), Theme.Ink);

            var export = Theme.Button("Guardar recibo", ButtonVariant.Secondary);
            export.Click += (s, e) => ExportReceipt(receipt);
            var done = Theme.Button("Listo", ButtonVariant.Primary);
            done.DialogResult = DialogResult.OK;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(null, done, export));
            AcceptButton = done;
            Theme.FitDialogToWorkingArea(this);
        }

        private static void AddSpan(TableLayoutPanel layout, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control);
            layout.SetColumnSpan(control, 2);
        }

        private static void AddDetail(TableLayoutPanel layout, string label, string value, Color valueColor)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = label, Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.Space.Sm, 0, Theme.Space.Sm) });
            layout.Controls.Add(new Label { Text = value, Font = Theme.Subhead, ForeColor = valueColor, AutoSize = false, Height = Theme.Subhead.Height + 2, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Xs) });
        }

        // Nombre legible de id_promocion_aplicada para recibos y comprobantes.
        internal static string PromotionName(string promotionId)
        {
            return promotionId == "SEXTA_VISITA" ? "6ª visita gratis" : promotionId == "GRUPAL" ? "Descuento grupal" : "Promoción";
        }

        private void ExportReceipt(Receipt receipt)
        {
            using (var save = new SaveFileDialog
            {
                Filter = "Archivo de texto (*.txt)|*.txt",
                FileName = "Recibo-2-" + receipt.Number + ".txt"
            })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;
                var text = new StringBuilder();
                text.AppendLine(AppData.Settings.StoreName);
                text.AppendLine("RECIBO #2-" + receipt.Number);
                if (receipt.Comprobante != null) text.AppendLine("BOLETA " + receipt.Comprobante.Documento + " (SIMULADA, no enviada a SUNAT)");
                text.AppendLine(receipt.CreatedAt.ToString("dd/MM/yyyy HH:mm"));
                text.AppendLine(new string('-', 42));
                foreach (var line in receipt.Lines)
                    text.AppendLine(line.Quantity + " x " + line.Name + "  " + Theme.Money(line.LineTotal));
                text.AppendLine(new string('-', 42));
                if (receipt.Discount > 0m)
                {
                    text.AppendLine("SUBTOTAL: " + Theme.Money(receipt.Subtotal));
                    text.AppendLine("DESCUENTO (" + PromotionName(receipt.PromotionId) + "): -" + Theme.Money(receipt.Discount));
                }
                text.AppendLine("TOTAL: " + Theme.Money(receipt.Total));
                text.AppendLine("PAGO: " + receipt.PaymentMethod);
                text.AppendLine("CLIENTE: " + receipt.CustomerName);
                if (!string.IsNullOrWhiteSpace(receipt.CustomerDni)) text.AppendLine("DNI: " + receipt.CustomerDni);
                File.WriteAllText(save.FileName, text.ToString(), Encoding.UTF8);
                Theme.Message(this, Tone.Success, "Recibo guardado correctamente.", "Recibo");
            }
        }
    }

    public sealed class SaveTicketDialog : Form
    {
        private readonly TextBox nameBox;
        private readonly Button lockerButton;
        private readonly Button acceptButton;
        private readonly Label lockerHint;
        private readonly int adultCount;
        private readonly int childCount;
        private readonly string editingTicketId;
        public string TicketName { get { return nameBox.Text.Trim(); } }
        public List<int> LockerNumbers { get; private set; } = new List<int>();
        public int AdultCount { get { return adultCount; } }
        public int ChildCount { get { return childCount; } }

        public SaveTicketDialog(
            string suggestedName,
            IEnumerable<int> initialLockers = null,
            int suggestedAdults = 0,
            int suggestedChildren = 0,
            string currentTicketId = null)
        {
            editingTicketId = currentTicketId;
            adultCount = Math.Max(0, Math.Min(999, suggestedAdults));
            childCount = Math.Max(0, Math.Min(999, suggestedChildren));
            LockerNumbers = (initialLockers ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x).ToList();
            var updating = !string.IsNullOrWhiteSpace(editingTicketId);
            DialogKit.Prepare(this, updating ? "Actualizar ticket" : "Guardar ticket", 540, 420);

            var body = DialogKit.Body();
            var stack = DialogKit.Stack();
            body.Controls.Add(stack);
            DialogKit.Row(stack, Theme.LabelField("Nombre para identificarlo"), 0, Theme.Space.Xs);
            nameBox = new TextBox { Font = Theme.Body, Text = suggestedName ?? "", MaxLength = 60, Dock = DockStyle.Fill };
            var nameField = Theme.Field(nameBox, Theme.Dim.Field);
            nameField.Dock = DockStyle.Fill;
            DialogKit.Row(stack, nameField, 0, Theme.Space.Lg);

            DialogKit.Row(stack, Theme.LabelField("Candado asignado"), 0, Theme.Space.Xs);
            lockerButton = Theme.Button("Seleccionar candado", ButtonVariant.Secondary);
            lockerButton.Dock = DockStyle.Fill;
            lockerButton.Click += SelectLocker;
            DialogKit.Row(stack, lockerButton, 0, Theme.Space.Sm);
            lockerHint = Theme.Label("", 9f, FontStyle.Regular, Theme.Muted);
            DialogKit.Row(stack, lockerHint, 0, Theme.Space.Md);
            DialogKit.Row(stack, Theme.Label("Ocupación estimada: " + adultCount + (adultCount == 1 ? " adulto" : " adultos") + " · " + childCount + (childCount == 1 ? " niño" : " niños"), 9f, FontStyle.Regular, Theme.Muted));
            UpdateLockerButton();

            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary); cancel.DialogResult = DialogResult.Cancel;
            acceptButton = Theme.Button(updating ? "Guardar cambios" : "Guardar", ButtonVariant.Primary);
            acceptButton.Enabled = LockerNumbers.Count > 0;
            acceptButton.Click += Accept_Click;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(null, acceptButton, cancel));
            Controls.Add(DialogKit.Header(updating ? "Actualizar ticket" : "Guardar ticket", "Se guardará para cobrarlo después."));
            CancelButton = cancel; AcceptButton = acceptButton;
            Shown += (s, e) => { nameBox.SelectAll(); nameBox.Focus(); };
            Theme.FitDialogToWorkingArea(this);
        }

        private void SelectLocker(object sender, EventArgs e)
        {
            var occupied = AppData.SavedTickets
                .Where(x => x.Id != editingTicketId)
                .SelectMany(AppData.GetTicketLockers)
                .Distinct()
                .ToList();
            using (var dialog = new LockerSelectionDialog(occupied, LockerNumbers, AppData.LockersOutOfService))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                LockerNumbers = dialog.SelectedLockers.ToList();
                UpdateLockerButton();
                acceptButton.Enabled = true;
            }
        }

        private void UpdateLockerButton()
        {
            if (LockerNumbers.Count == 0)
            {
                lockerButton.Text = "Seleccionar candado";
                lockerHint.Text = "Elija al menos un candado para poder guardar.";
                lockerHint.ForeColor = Theme.Warning;
                return;
            }
            lockerButton.Text = "✓  " + LockerNumbers.Count + (LockerNumbers.Count == 1 ? " llave: " : " llaves: ") + string.Join(", ", LockerNumbers) + "   ·   Cambiar";
            lockerHint.Text = "Candado asignado.";
            lockerHint.ForeColor = Theme.Success;
        }

        private void Accept_Click(object sender, EventArgs e)
        {
            if (!Validation.IsValidRequiredText(nameBox.Text, 2, 60))
            {
                Theme.Message(this, Tone.Warning, "Ingrese un nombre de 2 a 60 caracteres.", "Guardar ticket"); nameBox.Focus(); return;
            }
            if (LockerNumbers == null || LockerNumbers.Count == 0)
            {
                Theme.Message(this, Tone.Warning, "Seleccione uno de los 35 candados.", "Guardar ticket"); return;
            }
            DialogResult = DialogResult.OK; Close();
        }
    }

    internal enum LockerState { Free, Selected, Occupied, OutOfService }

    // Candado: libre / seleccionado / ocupado, distinguibles por forma y texto además del color.
    internal sealed class LockerTile : Control
    {
        private LockerState state;
        private bool hover, pressed;
        private static readonly Font CaptionFont = Theme.Caption;

        public LockerTile(int number)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Number = number;
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.Emphasis;
        }

        public int Number { get; private set; }
        public LockerState State { get { return state; } set { state = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            Color fill, border, ink, caption;
            string label;
            switch (state)
            {
                case LockerState.Selected: fill = Theme.GreenDark; border = Theme.GreenDark; ink = Color.White; caption = Color.White; label = "✓ Seleccionado"; break;
                case LockerState.Occupied: fill = Theme.DangerSoft; border = Theme.Danger; ink = Theme.Danger; caption = Theme.Danger; label = "Ocupado"; break;
                case LockerState.OutOfService: fill = Theme.Canvas; border = Theme.Warning; ink = Theme.Muted; caption = Theme.Warning; label = "Fuera de uso"; break;
                default: fill = pressed ? Theme.SoftPressed : hover ? Theme.GreenSoft : Theme.Surface; border = Theme.BorderStrong; ink = Theme.Ink; caption = Theme.Muted; label = "Libre"; break;
            }
            using (var path = Gfx.Round(rect, Theme.Radius.Control + 2))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(border, state == LockerState.Free ? 1f : 2f)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                using (var ring = Gfx.Round(new Rectangle(4, 4, Width - 9, Height - 9), Theme.Radius.Control))
                using (var pen = new Pen(state == LockerState.Selected ? Color.White : Theme.Primary, 1f) { DashStyle = DashStyle.Dot }) g.DrawPath(pen, ring);
            var numberHeight = Font.Height;
            var captionHeight = CaptionFont.Height;
            var top = (Height - numberHeight - captionHeight) / 2;
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, Number.ToString(), Font, new Rectangle(0, top, Width, numberHeight), ink, flags);
            TextRenderer.DrawText(g, label, CaptionFont, new Rectangle(2, top + numberHeight, Width - 4, captionHeight), caption, flags);
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); Update(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }
    }

    public sealed class LockerSelectionDialog : Form
    {
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly Label selectionLabel;
        public List<int> SelectedLockers { get { return selected.OrderBy(x => x).ToList(); } }

        public LockerSelectionDialog(System.Collections.Generic.IEnumerable<int> occupiedLockers, System.Collections.Generic.IEnumerable<int> selectedLockers = null,
            System.Collections.Generic.IEnumerable<int> outOfServiceLockers = null)
        {
            DialogKit.Prepare(this, "Seleccionar candado", 740, 600);
            var occupied = new System.Collections.Generic.HashSet<int>(occupiedLockers ?? new int[0]);
            // RN-06: llaves no devueltas que la dueña aún no liberó.
            var outOfService = new System.Collections.Generic.HashSet<int>(outOfServiceLockers ?? new int[0]);
            selected.UnionWith((selectedLockers ?? new int[0]).Where(x => x >= 1 && x <= 35));
            occupied.ExceptWith(selected);

            var body = DialogKit.Body();
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 5, Padding = new Padding(0), Margin = new Padding(0) };
            for (var column = 0; column < 7; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7f));
            for (var row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
            for (var number = 1; number <= 35; number++)
            {
                var tile = new LockerTile(number) { Dock = DockStyle.Fill, Margin = new Padding(Theme.Space.Xs) };
                if (outOfService.Contains(number) && !selected.Contains(number))
                {
                    tile.State = LockerState.OutOfService;
                    tile.Click += (s, e) => Theme.Message(this, Tone.Warning, "La llave " + ((LockerTile)s).Number + " está fuera de uso porque no se devolvió. La dueña puede liberarla en Configuración > Cargos por pérdida.", "Llave fuera de uso");
                }
                else if (occupied.Contains(number))
                {
                    tile.State = LockerState.Occupied;
                    tile.Click += (s, e) => Theme.Message(this, Tone.Info, "El candado " + ((LockerTile)s).Number + " está ocupado. Seleccione uno libre.", "Candado ocupado");
                }
                else
                {
                    tile.State = selected.Contains(number) ? LockerState.Selected : LockerState.Free;
                    tile.Click += ToggleLocker;
                }
                grid.Controls.Add(tile, (number - 1) % 7, (number - 1) / 7);
            }
            var legend = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Dim.Pill + Theme.Space.Md, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            var free = Theme.Pill("Libre", Tone.Neutral); free.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            var chosen = Theme.Pill("✓ Seleccionado", Tone.Brand); chosen.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            var busy = Theme.Pill("Ocupado", Tone.Danger); busy.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            var lost = Theme.Pill("Fuera de uso", Tone.Warning);
            legend.Controls.Add(free); legend.Controls.Add(chosen); legend.Controls.Add(busy);
            if (outOfService.Count > 0) legend.Controls.Add(lost);
            body.Controls.Add(grid);
            body.Controls.Add(legend);

            selectionLabel = Theme.Label("", 10f, FontStyle.Bold, Theme.Muted);
            selectionLabel.AutoSize = false; selectionLabel.Width = 260; selectionLabel.TextAlign = ContentAlignment.MiddleLeft;
            UpdateSelectionLabel();
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary); cancel.DialogResult = DialogResult.Cancel;
            var confirm = Theme.Button("Confirmar", ButtonVariant.Primary); confirm.Click += ConfirmSelection;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(selectionLabel, confirm, cancel));
            Controls.Add(DialogKit.Header("Seleccionar candados", "Toque uno o más candados libres."));
            CancelButton = cancel;
            Theme.FitDialogToWorkingArea(this);
        }

        private void ToggleLocker(object sender, EventArgs e)
        {
            var tile = (LockerTile)sender;
            if (selected.Contains(tile.Number)) { selected.Remove(tile.Number); tile.State = LockerState.Free; }
            else { selected.Add(tile.Number); tile.State = LockerState.Selected; }
            UpdateSelectionLabel();
        }

        private void UpdateSelectionLabel()
        {
            selectionLabel.Text = selected.Count == 0 ? "Ningún candado seleccionado" : selected.Count + (selected.Count == 1 ? " candado seleccionado" : " candados seleccionados");
            selectionLabel.ForeColor = selected.Count == 0 ? Theme.Muted : Theme.Success;
        }

        private void ConfirmSelection(object sender, EventArgs e)
        {
            if (selected.Count == 0) { Theme.Message(this, Tone.Info, "Seleccione al menos un candado libre.", "Seleccionar candados"); return; }
            DialogResult = DialogResult.OK; Close();
        }
    }

    public sealed class RentalReminderDialog : Form
    {
        public RentalReminderDialog()
        {
            DialogKit.Prepare(this, "Verificación antes de cobrar", 560, 400);
            var body = DialogKit.Body();
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            layout.Controls.Add(new Panel { Margin = new Padding(0) }, 0, 0);
            layout.Controls.Add(new Label { Text = "¿Alquilaron alguna prenda?", Font = Theme.Heading, ForeColor = Theme.Ink, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, Theme.Space.Md) }, 0, 1);
            layout.Controls.Add(new Label { Text = "Pedir las llaves", Font = Theme.Display, ForeColor = Theme.Danger, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0) }, 0, 2);
            layout.Controls.Add(new Panel { Margin = new Padding(0) }, 0, 3);
            body.Controls.Add(layout);

            var back = Theme.Button("Volver", ButtonVariant.Secondary); back.DialogResult = DialogResult.Cancel;
            var confirm = Theme.Button("Ya verifiqué", ButtonVariant.Primary); confirm.DialogResult = DialogResult.OK;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(null, confirm, back));
            Controls.Add(DialogKit.Header("Antes de cobrar", "Verifique la devolución antes de continuar con el pago."));
            CancelButton = back; AcceptButton = confirm;
            Theme.FitDialogToWorkingArea(this);
        }
    }

    public sealed class PaymentDateSummaryDialog : Form
    {
        private readonly DateTimePicker datePicker;
        private readonly Label cashValue;
        private readonly Label yapeValue;
        private readonly Label ticketCount;
        private readonly Banner emptyBanner;

        public PaymentDateSummaryDialog(DateTime initialDate)
        {
            DialogKit.Prepare(this, "Resumen de cobros por fecha", 560, 470);
            var body = DialogKit.Body();
            var stack = DialogKit.Stack();
            body.Controls.Add(stack);
            DialogKit.Row(stack, Theme.LabelField("Fecha"), 0, Theme.Space.Xs);
            datePicker = new DateTimePicker { Font = Theme.Body, Format = DateTimePickerFormat.Custom, CustomFormat = "dddd dd/MM/yyyy", Value = initialDate.Date };
            datePicker.ValueChanged += (s, e) => UpdateTotals();
            var dateField = Theme.Field(datePicker, Theme.Dim.Field);
            dateField.Dock = DockStyle.Fill;
            DialogKit.Row(stack, dateField, 0, Theme.Space.Lg);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = 104, Margin = new Padding(0), Padding = new Padding(0) };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cards.Controls.Add(MetricCard("Efectivo", out cashValue, new Padding(0, 0, Theme.Space.Sm, 0)), 0, 0);
            cards.Controls.Add(MetricCard("Yape", out yapeValue, new Padding(Theme.Space.Sm, 0, 0, 0)), 1, 0);
            DialogKit.Row(stack, cards, 0, Theme.Space.Lg);
            ticketCount = Theme.Label("", 10.5f, FontStyle.Regular, Theme.Muted);
            DialogKit.Row(stack, ticketCount, 0, Theme.Space.Md);
            emptyBanner = Theme.Banner("No hay cobros pagados en la fecha seleccionada.", Tone.Info);
            emptyBanner.Dock = DockStyle.Fill;
            emptyBanner.Visible = false;
            DialogKit.Row(stack, emptyBanner);

            var close = Theme.Button("Cerrar", ButtonVariant.Primary); close.DialogResult = DialogResult.OK;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(null, close));
            Controls.Add(DialogKit.Header("Resumen de cobros por fecha", "Efectivo y Yape cobrados en el día."));
            AcceptButton = close; CancelButton = close; UpdateTotals();
            Theme.FitDialogToWorkingArea(this);
        }

        private static Control MetricCard(string caption, out Label value, Padding margin)
        {
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = margin };
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Lg) };
            value = new Label { Dock = DockStyle.Fill, Font = Theme.Display, ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            card.Controls.Add(value);
            card.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = Theme.FieldLabel.Height + 4, Font = Theme.FieldLabel, ForeColor = Theme.Muted, BackColor = Color.Transparent });
            host.Controls.Add(card);
            return host;
        }

        private void UpdateTotals()
        {
            var date = datePicker.Value.Date;
            List<Receipt> paid;
            try
            {
                paid = AppData.Receipts.Where(x => x.Status == "Pagado" && x.CreatedAt.Date == date).ToList();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Resumen de pagos por fecha (base de datos)");
                cashValue.Text = "—"; yapeValue.Text = "—"; ticketCount.Text = "No se pudieron leer los recibos.";
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Resumen de pagos");
                return;
            }
            var cash = paid.Sum(x => x.CashPaid);
            var yape = paid.Sum(x => x.YapePaid);
            cashValue.Text = Theme.Money(cash);
            yapeValue.Text = Theme.Money(yape);
            ticketCount.Text = paid.Count + (paid.Count == 1 ? " recibo pagado" : " recibos pagados") + " · Total " + Theme.Money(cash + yape);
            emptyBanner.Visible = paid.Count == 0;
        }
    }

    public sealed class SavedTicketsDialog : Form
    {
        private readonly DataGridView grid;
        private readonly TextBox searchBox;
        private readonly Label adultsValue;
        private readonly Label childrenValue;
        private readonly Label totalValue;
        private readonly EmptyState emptyState;
        public SavedTicket SelectedTicket { get; private set; }

        public SavedTicketsDialog()
        {
            Text = "Tickets guardados";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(940, 640);
            MinimumSize = new Size(720, 500);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Surface;
            Font = Theme.Body;

            // Tabla (centro)
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Lg), BackColor = Theme.Surface };
            var tableCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            grid = new DataGridView { Dock = DockStyle.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Locker", HeaderText = "Candado", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Font = Theme.Subhead } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Ticket", FillWeight = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Customer", HeaderText = "Cliente", FillWeight = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Adults", HeaderText = "Adultos", Width = 84, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Children", HeaderText = "Niños", Width = 76, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "Ingreso", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            // RF-20: tiempo que lleva la visita (desde que se guardó el ticket) y saldo pendiente de cobro.
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Elapsed", HeaderText = "Tiempo", Width = 96, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "Por cobrar", Width = 110, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, Font = Theme.Subhead } });
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenSelected(); };
            emptyState = Theme.EmptyState("No hay tickets guardados", "Guarde un ticket desde Ventas y aparecerá aquí.");
            emptyState.Visible = false;
            tableCard.Controls.Add(emptyState);
            tableCard.Controls.Add(grid);
            host.Controls.Add(tableCard);

            // Acciones (abajo): primaria a la derecha
            var cancel = Theme.Button("Cerrar", ButtonVariant.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            var delete = Theme.Button("Eliminar", ButtonVariant.Destructive);
            delete.Width = 120;
            delete.Click += (s, e) => DeleteSelected();
            var editLockers = Theme.Button("Editar llaves", ButtonVariant.Secondary);
            editLockers.Click += (s, e) => EditSelectedLockers();
            var open = Theme.Button("Abrir ticket", ButtonVariant.Primary);
            open.Click += (s, e) => OpenSelected();
            var actions = DialogKit.Actions(delete, open, editLockers, cancel);

            // Ocupación
            var stats = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Md), BackColor = Theme.Surface };
            var statsCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Lg, Theme.Space.Sm, Theme.Space.Lg, Theme.Space.Sm) };
            var statsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            for (var i = 0; i < 3; i++) statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            statsLayout.Controls.Add(StatBlock("Adultos", out adultsValue), 0, 0);
            statsLayout.Controls.Add(StatBlock("Niños", out childrenValue), 1, 0);
            statsLayout.Controls.Add(StatBlock("Total en el local", out totalValue), 2, 0);
            statsCard.Controls.Add(statsLayout);
            stats.Controls.Add(statsCard);

            // Búsqueda
            var searchPanel = new Panel { Dock = DockStyle.Top, Height = Theme.Dim.Field + Theme.Space.Md, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Md), BackColor = Theme.Surface };
            searchBox = new TextBox { Font = Theme.Body, Tag = "Buscar por nombre, DNI o número de candado", Dock = DockStyle.Fill, AccessibleName = "Buscar por nombre, DNI o número de candado" };
            var searchField = Theme.Field(searchBox, Theme.Dim.Field);
            searchField.Dock = DockStyle.Fill;
            searchBox.TextChanged += (s, e) => LoadTickets();
            searchPanel.Controls.Add(searchField);

            Controls.Add(host);
            Controls.Add(actions);
            Controls.Add(searchPanel);
            Controls.Add(stats);
            Controls.Add(DialogKit.Header("Tickets guardados", "Clientes dentro del local."));
            LoadTickets();
            CancelButton = cancel;
            AcceptButton = open;
            Theme.FitDialogToWorkingArea(this);
            // El tiempo transcurrido se actualiza cada 30 s mientras el diálogo está abierto (sin volver a consultar la base).
            elapsedTimer = new Timer { Interval = 30000 };
            elapsedTimer.Tick += (s, e) => RefreshElapsed();
            elapsedTimer.Start();
            FormClosed += (s, e) => elapsedTimer.Dispose();
        }

        private readonly Timer elapsedTimer;

        // "45 min", "1 h 05 min". Si el reloj de la PC quedó atrás de la hora guardada, muestra "0 min".
        internal static string ElapsedText(DateTime since)
        {
            var minutes = Math.Max(0, (int)(DateTime.Now - since).TotalMinutes);
            return minutes < 60 ? minutes + " min" : (minutes / 60) + " h " + (minutes % 60).ToString("00") + " min";
        }

        private void RefreshElapsed()
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var ticket = row.Tag as SavedTicket;
                if (ticket != null) row.Cells["Elapsed"].Value = ElapsedText(ticket.CreatedAt);
            }
        }

        private static Control StatBlock(string caption, out Label value)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            value = new Label { Dock = DockStyle.Fill, Font = Theme.Heading, ForeColor = Theme.Ink, TextAlign = ContentAlignment.TopLeft, BackColor = Color.Transparent };
            panel.Controls.Add(value);
            panel.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = Theme.FieldLabel.Height + 4, Font = Theme.FieldLabel, ForeColor = Theme.Muted, BackColor = Color.Transparent });
            return panel;
        }

        private SavedTicket Current()
        {
            return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as SavedTicket;
        }

        private void LoadTickets()
        {
            var allTickets = AppData.SavedTickets.OrderByDescending(x => x.CreatedAt).ToList();
            var adults = allTickets.Sum(AppData.GetTicketAdultCount);
            var children = allTickets.Sum(AppData.GetTicketChildCount);
            adultsValue.Text = adults.ToString();
            childrenValue.Text = children.ToString();
            totalValue.Text = (adults + children).ToString();
            var term = (searchBox.Text ?? "").Trim();
            var tickets = allTickets.AsEnumerable();
            if (term.Length > 0)
            {
                int lockerQuery;
                var searchesLocker = int.TryParse(term, out lockerQuery);
                tickets = tickets.Where(ticket =>
                    (ticket.Name ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    (ticket.CustomerName ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    (ticket.CustomerDni ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (searchesLocker && AppData.GetTicketLockers(ticket).Contains(lockerQuery)));
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var ticket in tickets)
            {
                var customer = ticket.CustomerName + (string.IsNullOrWhiteSpace(ticket.CustomerDni) ? "" : " • " + ticket.CustomerDni);
                var lockers = AppData.GetTicketLockers(ticket);
                var row = grid.Rows.Add(
                    lockers.Count > 0 ? string.Join(", ", lockers) : "—",
                    ticket.Name,
                    customer,
                    AppData.GetTicketAdultCount(ticket),
                    AppData.GetTicketChildCount(ticket),
                    ticket.CreatedAt.ToString("dd/MM HH:mm"),
                    ElapsedText(ticket.CreatedAt),
                    Theme.Money(ticket.Total));
                grid.Rows[row].Tag = ticket;
            }
            if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
            grid.ResumeLayout();
            var none = grid.Rows.Count == 0;
            emptyState.Title = term.Length > 0 ? "Sin resultados" : "No hay tickets guardados";
            emptyState.Hint = term.Length > 0 ? "Pruebe con otro nombre, DNI o número de candado." : "Guarde un ticket desde Ventas y aparecerá aquí.";
            emptyState.Visible = none;
            grid.Visible = !none;
        }

        private void DeleteSelected()
        {
            var ticket = Current();
            if (ticket == null) return;
            if (MessageBox.Show("¿Eliminar el ticket guardado ‘" + ticket.Name + "’?", "Tickets guardados", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                AppData.DeleteSavedTicket(ticket.Id);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Eliminar ticket guardado (base de datos)");
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Tickets guardados");
                return;
            }
            LoadTickets();
        }

        private void EditSelectedLockers()
        {
            var ticket = Current();
            if (ticket == null)
            {
                Theme.Message(this, Tone.Info, "Seleccione un ticket para editar su llave.", "Tickets guardados");
                return;
            }

            var occupied = AppData.SavedTickets
                .Where(x => x.Id != ticket.Id)
                .SelectMany(AppData.GetTicketLockers)
                .Distinct()
                .ToList();
            using (var dialog = new LockerSelectionDialog(occupied, AppData.GetTicketLockers(ticket), AppData.LockersOutOfService))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    AppData.UpdateSavedTicketLockers(ticket.Id, dialog.SelectedLockers);
                    LoadTickets();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(this, Tone.Warning, ex.Message, "Editar llave");
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Editar llave (base de datos)");
                    Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Editar llave");
                }
            }
        }

        private void OpenSelected()
        {
            SelectedTicket = Current();
            if (SelectedTicket == null)
            {
                Theme.Message(this, Tone.Info, "No hay un ticket seleccionado.", "Tickets guardados");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    public static class PromptDialog
    {
        public static bool AskText(IWin32Window owner, string title, string label, out string value, string initial = "")
        {
            using (var form = BaseForm(title, 450, 220))
            {
                var caption = Theme.LabelField(label);
                caption.Location = new Point(24, 24);
                var box = new TextBox { Location = new Point(24, 52), Size = new Size(402, 34), Font = Theme.Body, Text = initial };
                var field = Theme.Field(box, Theme.Dim.Field);
                var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary); cancel.SetBounds(154, 125, 128, 46); cancel.DialogResult = DialogResult.Cancel;
                var ok = Theme.Button("Aceptar", ButtonVariant.Primary); ok.SetBounds(292, 125, 134, 46); ok.DialogResult = DialogResult.OK;
                form.Controls.Add(caption); form.Controls.Add(field); form.Controls.Add(cancel); form.Controls.Add(ok);
                form.AcceptButton = ok; form.CancelButton = cancel;
                var result = form.ShowDialog(owner) == DialogResult.OK;
                value = box.Text.Trim();
                return result;
            }
        }

        public static bool AskMoney(IWin32Window owner, string title, string label, out decimal value, decimal initial = 0m)
        {
            using (var form = BaseForm(title, 450, 230))
            {
                var caption = Theme.LabelField(label);
                caption.Location = new Point(24, 24);
                var input = new NumericUpDown
                {
                    Location = new Point(24, 52), Size = new Size(402, 38), Font = Theme.Emphasis,
                    DecimalPlaces = 2, Maximum = 1000000, Minimum = 0, Value = Math.Min(1000000, Math.Max(0, initial)), ThousandsSeparator = true
                };
                var field = Theme.Field(input, Theme.Dim.Control);
                var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary); cancel.SetBounds(154, 135, 128, 46); cancel.DialogResult = DialogResult.Cancel;
                var ok = Theme.Button("Aceptar", ButtonVariant.Primary); ok.SetBounds(292, 135, 134, 46); ok.DialogResult = DialogResult.OK;
                form.Controls.Add(caption); form.Controls.Add(field); form.Controls.Add(cancel); form.Controls.Add(ok);
                form.AcceptButton = ok; form.CancelButton = cancel;
                var result = form.ShowDialog(owner) == DialogResult.OK;
                value = input.Value;
                return result;
            }
        }

        private static Form BaseForm(string title, int width, int height)
        {
            var form = new Form
            {
                Text = title, ClientSize = new Size(width, height), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi, BackColor = Theme.Surface, Font = Theme.Body, ShowInTaskbar = false
            };
            Theme.FitDialogToWorkingArea(form);
            return form;
        }
    }
}
