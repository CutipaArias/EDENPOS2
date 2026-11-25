using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class ProductOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
        public int Stock { get; set; }
        public int InitialStock { get; set; }
        public bool TrackInventory { get; set; }
        public int StockMinimo { get; set; }
    }

    public static class ProductOptionCatalog
    {
        private static readonly Dictionary<string, List<ProductOption>> Templates =
            new Dictionary<string, List<ProductOption>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GASEOSAS", NewOptions(
                        "INCA COLA / 600 ML", 4m,
                        "INCA COLA / 2.5 L", 13m,
                        "COCA COLA / 600 ML", 4m,
                        "COCA COLA / 2.5 L", 13m)
                },
                {
                    "HIDRATANTE", NewOptions(
                        "CIELO / 625 ML", 1.50m,
                        "CIELO / 1 L", 3m,
                        "CIELO / 2.5 L", 5m,
                        "VOLT / 300 ML", 4m,
                        "SPORADE / 1 L", 5m,
                        "SPORADE / 500 ML", 3.50m)
                },
                {
                    "JARRAS", NewOptions(
                        "MARACUYÁ / 1.5 L", 7m,
                        "MARACUYÁ / 2 L", 9m,
                        "LIMONADA / 1.5 L", 7m,
                        "LIMONADA / 2 L", 9m,
                        "LIMONADA / 1 L", 5m,
                        "PIÑA / 1.5 L", 10m,
                        "PIÑA / 2 L", 13m)
                },
                {
                    "SANDWICHES", NewOptions(
                        "TOSTADA DE QUESO", 2.50m,
                        "SÁNDWICH DE POLLO", 5m,
                        "CHORIPÁN", 6m)
                },
                {
                    "SNACKS", NewOptions(
                        "PAPRIKA", 2m,
                        "TENTACIÓN", 1.50m,
                        "NIK", 1.50m,
                        "OREO", 1.50m,
                        "RITZ", 1.50m)
                },
                {
                    "ACONDICIONADOR", NewOptions(
                        "BONAWELL", 2m,
                        "NUTRIBELA", 2m)
                },
                {
                    "JABONES", NewOptions(
                        "HOTELERO", 1m,
                        "FRUTAL", 2.50m,
                        "REXONA", 4.50m,
                        "PALMOLIVE", 4.50m)
                },
                {
                    "SHAMPOOS", NewOptions(
                        "H&S", 2m,
                        "NUTRIBELA", 2m,
                        "PANTENE", 2m,
                        "SEDAL", 2m,
                        "AMARAS", 2m,
                        "H&S MINI", 1m)
                }
            };

        public static bool HasOptions(Product product)
        {
            return product != null &&
                   (product.Options != null ? product.Options.Count > 0 : Templates.ContainsKey(product.Name ?? ""));
        }

        public static IReadOnlyList<ProductOption> GetOptions(Product product)
        {
            if (product == null) return new List<ProductOption>();
            if (product.Options != null) return product.Options;
            List<ProductOption> options;
            return Templates.TryGetValue(product.Name ?? "", out options) ? options : new List<ProductOption>();
        }

        public static bool HasTemplate(string productName)
        {
            return Templates.ContainsKey(productName ?? "");
        }

        public static List<ProductOption> CreateDefaultOptions(string productName)
        {
            List<ProductOption> templates;
            if (!Templates.TryGetValue(productName ?? "", out templates)) return null;
            return templates.Select(option => new ProductOption
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = option.Name,
                Price = option.Price
            }).ToList();
        }

        private static List<ProductOption> NewOptions(params object[] values)
        {
            var result = new List<ProductOption>();
            for (var index = 0; index + 1 < values.Length; index += 2)
                result.Add(new ProductOption { Name = (string)values[index], Price = (decimal)values[index + 1] });
            return result;
        }
    }

    public static class OccupancyCalculator
    {
        public static void Count(IEnumerable<ReceiptLine> lines, out int adults, out int children)
        {
            List<string> unmatched;
            Count(lines, out adults, out children, out unmatched);
        }

        public static void Count(IEnumerable<ReceiptLine> lines, out int adults, out int children, out List<string> unmatchedNames)
        {
            adults = 0;
            children = 0;
            unmatchedNames = new List<string>();
            foreach (var line in lines ?? Enumerable.Empty<ReceiptLine>())
            {
                var name = (line.Name ?? "").ToUpperInvariant();
                var quantity = Math.Max(0, line.Quantity);
                if (name.Contains("NIÑO"))
                {
                    children += quantity;
                    continue;
                }
                if (name.Contains("FAMILIA 1"))
                {
                    adults += quantity * 2;
                    children += quantity;
                }
                else if (name.Contains("FAMILIA 2"))
                {
                    adults += quantity * 2;
                    children += quantity * 2;
                }
                else if (name.Contains("AMIGOS X5")) adults += quantity * 5;
                else if (name.Contains("AMIGOS X4") || name.Contains("MUJER 4")) adults += quantity * 4;
                else if (name.Contains("AMIGOS X3") || name.Contains("MUJER 3")) adults += quantity * 3;
                else if (name.Contains("PAREJA") || name.Contains("MUJER 2")) adults += quantity * 2;
                else if (name.Contains("GENERAL") || name.Contains("ADULTO MAYOR") ||
                         name.Contains("VIP") || name.Contains("PASE CORTESÍA"))
                    adults += quantity;
                else if (quantity > 0 && !string.IsNullOrWhiteSpace(line.Name) && !unmatchedNames.Contains(line.Name))
                    unmatchedNames.Add(line.Name);
            }
        }
    }

    public sealed class ProductOptionsDialog : Form
    {
        private readonly Product product;
        private readonly FlowLayoutPanel optionList;
        private readonly NumericUpDown quantity;
        private readonly TextBox comment;
        private readonly Button save;
        private readonly Dictionary<Control, ProductOption> optionRows = new Dictionary<Control, ProductOption>();
        private readonly Dictionary<Control, Label> optionNames = new Dictionary<Control, Label>();
        private ProductOption selected;

        public ProductOption SelectedOption { get { return selected; } }
        public int SelectedQuantity { get { return (int)quantity.Value; } }
        public string SelectedComment { get { return (comment.Text ?? "").Trim(); } }

        public ProductOptionsDialog(Product item)
        {
            product = item ?? throw new ArgumentNullException(nameof(item));
            Text = "Elegir " + product.Name;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(720, 650);
            MinimumSize = new Size(520, 480);
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Surface;
            WindowChrome.Integrate(this);
            Font = Theme.Body;

            var head = DialogKit.Header(product.Name, "Elija una opción para agregarla al ticket.");

            // Cantidad y comentario
            var inputs = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(Theme.Space.Xl, Theme.Space.Md, Theme.Space.Xl, Theme.Space.Lg),
                BackColor = Theme.Surface
            };
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130f));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var quantityLabel = Theme.LabelField("Cantidad");
            quantityLabel.Margin = new Padding(0, 0, 0, Theme.Space.Xs);
            var commentLabel = Theme.LabelField("Comentario opcional");
            commentLabel.Margin = new Padding(0, 0, 0, Theme.Space.Xs);
            quantity = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 999,
                Value = 1,
                Font = Theme.Body,
                TextAlign = HorizontalAlignment.Center
            };
            var quantityField = Theme.Field(quantity, Theme.Dim.Field);
            quantityField.Dock = DockStyle.Fill;
            quantityField.Margin = new Padding(0, 0, Theme.Space.Md, 0);
            comment = new TextBox { Font = Theme.Body, MaxLength = 120, Tag = "Ej.: sin hielo" };
            var commentField = Theme.Field(comment, Theme.Dim.Field);
            commentField.Dock = DockStyle.Fill;
            commentField.Margin = new Padding(0);
            inputs.Controls.Add(quantityLabel, 0, 0);
            inputs.Controls.Add(commentLabel, 1, 0);
            inputs.Controls.Add(quantityField, 0, 1);
            inputs.Controls.Add(commentField, 1, 1);

            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            save = Theme.Button("Agregar al ticket", ButtonVariant.Primary);
            save.Enabled = false;
            save.Click += (s, e) =>
            {
                if (selected == null) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            var actions = DialogKit.Actions(null, save, cancel);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, Theme.Space.Sm, Theme.Space.Xl, Theme.Space.Sm), BackColor = Theme.Surface };
            optionList = new BufferedFlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = false,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(0),
                BackColor = Theme.Surface
            };
            optionList.SizeChanged += (s, e) => ResizeOptionRows();
            body.Controls.Add(optionList);

            Controls.Add(body);
            Controls.Add(inputs);
            Controls.Add(actions);
            Controls.Add(head);
            foreach (var option in ProductOptionCatalog.GetOptions(product)) AddOptionRow(option);
            ResizeOptionRows();
            CancelButton = cancel;
            AcceptButton = save;
            Theme.FitDialogToWorkingArea(this);
        }

        private void AddOptionRow(ProductOption option)
        {
            var row = new CardPanel
            {
                Height = 60,
                Width = 630,
                Margin = new Padding(0, 0, 0, Theme.Space.Sm),
                Cursor = Cursors.Hand,
                BackColor = Theme.Surface,
                Tag = option
            };
            var price = new Label
            {
                Text = Theme.Money(option.Price),
                Dock = DockStyle.Right,
                Width = 130,
                ForeColor = Theme.Ink,
                Font = Theme.Emphasis,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, Theme.Space.Lg, 0),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            var name = new Label
            {
                Text = option.Name,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Ink,
                Font = Theme.Subhead,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Theme.Space.Lg, 0, Theme.Space.Sm, 0),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            row.Controls.Add(name);
            row.Controls.Add(price);
            EventHandler choose = (s, e) => SelectOption(row, option);
            row.Click += choose;
            name.Click += choose;
            price.Click += choose;
            optionRows[row] = option;
            optionNames[row] = name;
            optionList.Controls.Add(row);
        }

        private void SelectOption(Control row, ProductOption option)
        {
            selected = option;
            foreach (var item in optionRows)
            {
                var selectedRow = item.Key == row;
                item.Key.BackColor = selectedRow ? Theme.GreenSoft : Theme.Surface;
                var card = item.Key as CardPanel;
                if (card != null) card.BorderColor = selectedRow ? Theme.Primary : Theme.Border;
                foreach (Label label in item.Key.Controls.OfType<Label>())
                    label.ForeColor = selectedRow ? Theme.GreenDark : Theme.Ink;
                optionNames[item.Key].Text = (selectedRow ? "✓  " : "") + item.Value.Name;
                item.Key.Invalidate();
            }
            save.Enabled = true;
            save.Text = "Agregar  " + Theme.Money(option.Price);
        }

        private void ResizeOptionRows()
        {
            var width = Math.Max(300, optionList.ClientSize.Width - optionList.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control row in optionRows.Keys) row.Width = width;
        }
    }
}
