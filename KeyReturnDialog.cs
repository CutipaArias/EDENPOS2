using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    // RF-19: antes de cobrar una visita con llaves se confirma qué devolvió el cliente. Todo empieza marcado como devuelto; lo que se desmarca
    // se cobra con el cargo configurado (RN-07) y, si es una llave, queda fuera de uso (RN-06). El resultado es un LossReport; los montos
    // los vuelve a calcular AppData.AddReceipt.
    public sealed class KeyReturnDialog : Form
    {
        private sealed class KeyRow { public int Locker; public CheckBox Returned; }
        private sealed class ImplementRow { public Product Product; public ProductOption Option; public int Delivered; public CheckBox Returned; public NumericUpDown Missing; }

        private readonly List<KeyRow> keyRows = new List<KeyRow>();
        private readonly List<ImplementRow> implementRows = new List<ImplementRow>();
        private readonly decimal keyCharge;
        private readonly Label summary;

        public LossReport Report { get; private set; }

        public KeyReturnDialog(IEnumerable<int> lockers, IEnumerable<ReceiptLine> lines, List<Product> products, decimal keyChargeAmount)
        {
            keyCharge = keyChargeAmount;
            var lockerList = (lockers ?? new int[0]).Distinct().OrderBy(x => x).ToList();
            var implements = new List<ImplementRow>();
            foreach (var group in (lines ?? new ReceiptLine[0]).Where(x => !string.IsNullOrEmpty(x.ProductId) && x.Quantity > 0)
                         .GroupBy(x => x.ProductId + "\u0001" + (x.OptionId ?? "")))
            {
                var first = group.First();
                var product = (products ?? new List<Product>()).FirstOrDefault(x => x.Id == first.ProductId);
                if (!AppData.IsImplement(product)) continue;
                var option = string.IsNullOrEmpty(first.OptionId) ? null : (product.Options ?? new List<ProductOption>()).FirstOrDefault(x => x.Id == first.OptionId);
                if (!string.IsNullOrEmpty(first.OptionId) && option == null) continue;
                implements.Add(new ImplementRow { Product = product, Option = option, Delivered = group.Sum(x => x.Quantity) });
            }

            var rows = lockerList.Count + implements.Count;
            // Alto según las filas (FitDialogToWorkingArea lo ajusta a la pantalla si hiciera falta).
            DialogKit.Prepare(this, "Devolución de llaves e implementos", 620, 340 + rows * 46 + (implements.Count > 0 ? 40 : 0));
            var body = DialogKit.Body();
            var stack = DialogKit.Stack();
            body.Controls.Add(stack);

            DialogKit.Row(stack, Theme.LabelField("Llaves"), 0, Theme.Space.Xs);
            foreach (var locker in lockerList)
            {
                var row = new KeyRow { Locker = locker, Returned = MakeCheck("Llave N.º " + locker + " devuelta") };
                row.Returned.CheckedChanged += (s, e) => UpdateSummary();
                keyRows.Add(row);
                DialogKit.Row(stack, Line(row.Returned, null, "Si falta: " + Theme.Money(keyCharge)), 0, Theme.Space.Xs);
            }

            if (implements.Count > 0)
            {
                DialogKit.Row(stack, Theme.LabelField("Implementos de la visita"), Theme.Space.Lg, Theme.Space.Xs);
                foreach (var row in implements)
                {
                    var name = AppData.ImplementDisplayName(row.Product, row.Option);
                    row.Returned = MakeCheck(name + (row.Delivered > 1 ? " (" + row.Delivered + ") devueltos" : " devuelto"));
                    if (row.Delivered > 1)
                    {
                        row.Missing = new NumericUpDown { Font = Theme.Body, Minimum = 1, Maximum = row.Delivered, Value = row.Delivered, Enabled = false, Width = 70 };
                        row.Missing.ValueChanged += (s, e) => UpdateSummary();
                    }
                    var current = row;
                    row.Returned.CheckedChanged += (s, e) =>
                    {
                        if (current.Missing != null) current.Missing.Enabled = !current.Returned.Checked;
                        UpdateSummary();
                    };
                    implementRows.Add(row);
                    DialogKit.Row(stack, Line(row.Returned, row.Missing, "Si falta: " + Theme.Money(row.Product.CargoPerdida) + (row.Delivered > 1 ? " c/u" : "")), 0, Theme.Space.Xs);
                }
            }

            var note = Theme.Banner("Desmarque lo que el cliente no devolvió: el cargo configurado se suma al cobro. Una llave no devuelta queda fuera de uso hasta que la dueña la libere.", Tone.Info);
            note.Dock = DockStyle.Fill;
            DialogKit.Row(stack, note, Theme.Space.Lg, 0);

            summary = Theme.Label("", 10.5f, FontStyle.Bold, Theme.Muted);
            summary.AutoSize = false; summary.Width = 300; summary.TextAlign = ContentAlignment.MiddleLeft;
            var back = Theme.Button("Volver", ButtonVariant.Secondary); back.DialogResult = DialogResult.Cancel;
            var confirm = Theme.Button("Continuar al cobro", ButtonVariant.Primary); confirm.Click += Confirm_Click;
            Controls.Add(body);
            Controls.Add(DialogKit.Actions(summary, confirm, back));
            Controls.Add(DialogKit.Header("Devolución de llaves e implementos", "Confirme lo que el cliente devolvió antes de finalizar la visita."));
            CancelButton = back; AcceptButton = confirm;
            UpdateSummary();
            Theme.FitDialogToWorkingArea(this);
        }

        private static CheckBox MakeCheck(string text)
        {
            return new CheckBox { Text = text, Checked = true, Font = Theme.Subhead, ForeColor = Theme.Ink, BackColor = Theme.Surface, AutoSize = false, Height = 30, Dock = DockStyle.Fill, Cursor = Cursors.Hand, AutoEllipsis = true };
        }

        // Fila: casilla "devuelta", cantidad no devuelta (solo si se entregó más de una unidad) y el cargo si falta.
        private static Control Line(CheckBox check, NumericUpDown missing, string chargeText)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Height = 38, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, missing == null ? 0 : 150));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.Controls.Add(check, 0, 0);
            if (missing != null)
            {
                var holder = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Surface };
                var caption = Theme.Label("No devueltos", Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
                caption.Margin = new Padding(0, 8, Theme.Space.Xs, 0);
                holder.Controls.Add(caption);
                holder.Controls.Add(missing);
                row.Controls.Add(holder, 1, 0);
            }
            var charge = Theme.Label(chargeText, Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
            charge.AutoSize = false; charge.Dock = DockStyle.Fill; charge.TextAlign = ContentAlignment.MiddleRight;
            row.Controls.Add(charge, 2, 0);
            return row;
        }

        private LossReport BuildReport()
        {
            var report = new LossReport();
            report.LostKeys.AddRange(keyRows.Where(x => !x.Returned.Checked).Select(x => x.Locker));
            report.ReturnedKeys.AddRange(keyRows.Where(x => x.Returned.Checked).Select(x => x.Locker));   // el servidor exige devueltas + perdidas = llaves del ticket
            foreach (var row in implementRows.Where(x => !x.Returned.Checked))
                report.LostImplements.Add(new LostImplement
                {
                    ProductId = row.Product.Id,
                    OptionId = row.Option == null ? null : row.Option.Id,
                    Quantity = row.Missing == null ? row.Delivered : (int)row.Missing.Value
                });
            return report;
        }

        private void UpdateSummary()
        {
            var report = BuildReport();
            if (report.IsEmpty)
            {
                summary.Text = "Todo devuelto: sin cargos";
                summary.ForeColor = Theme.Success;
                return;
            }
            var total = report.LostKeys.Count * keyCharge +
                        report.LostImplements.Sum(x => implementRows.First(r => r.Product.Id == x.ProductId && (r.Option == null ? null : r.Option.Id) == x.OptionId).Product.CargoPerdida * x.Quantity);
            summary.Text = "Cargos por pérdida: " + Theme.Money(total);
            summary.ForeColor = Theme.Warning;
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            Report = BuildReport();
            if (!Report.IsEmpty)
            {
                var missing = Report.LostKeys.Select(x => "llave N.º " + x)
                    .Concat(implementRows.Where(x => !x.Returned.Checked).Select(x => AppData.ImplementDisplayName(x.Product, x.Option))).ToList();
                if (MessageBox.Show(this, "Se registrará como no devuelto: " + string.Join(", ", missing) + ".\r\n\r\n¿Desea continuar al cobro con el cargo por pérdida?",
                        "Confirmar pérdida", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
