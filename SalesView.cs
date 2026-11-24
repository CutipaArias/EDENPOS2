using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class SalesView : UserControl, IRefreshable
    {
        private readonly TextBox searchBox;
        private readonly FlowLayoutPanel categoryFlow;
        private readonly FlowLayoutPanel productFlow;
        private readonly EmptyState productEmpty;
        private readonly DataGridView cartGrid;
        private readonly EmptyState cartEmpty;
        private readonly Label totalLabel;
        private readonly Label itemCountLabel;
        private readonly TextBox customerBox;
        private readonly TextBox dniBox;
        private readonly TextBox phoneBox;
        private readonly CheckBox consentBox;
        private string customerSource;   // origen del nombre (consulta o registro) para guardarlo en el registro de clientes
        private readonly Button lookupDniButton;
        private readonly Label customerStatus;
        private readonly Button borrowedLockersLabel;
        private readonly Banner promoBanner;
        private readonly Timer dniLookupTimer;
        private readonly Button chargeButton;
        private readonly Button saveButton;
        private readonly Button savedTicketsButton;
        private readonly Timer productSearchTimer;
        private readonly Timer promoHintTimer;
        private int promoHintVersion;
        private volatile bool promoHintFailed;
        private int savedInfoVersion;
        private volatile bool savedInfoFailed;
        private volatile bool savedInfoPending;
        private int savedTicketCount;
        private Dictionary<string, SavedTicket> savedTicketsById = new Dictionary<string, SavedTicket>();
        private readonly List<ReceiptLine> cart = new List<ReceiptLine>();
        private List<Product> availableProducts = new List<Product>();
        private string selectedCategory;   // null = todos los artículos
        private bool lookupInProgress;
        private string lastAutoLookup;
        private string loadedSavedTicketId;
        private List<int> loadedLockerNumbers = new List<int>();
        private Point hoverCell = new Point(-1, -1);
        private Point pressedCell = new Point(-1, -1);

        private static readonly Font LineNameFont = Theme.Font(10.5f, FontStyle.Bold);

        public SalesView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Canvas;

            var header = new ScreenHeader { Title = "Ventas", Subtitle = "Toque un artículo para agregarlo al ticket" };
            Controls.Add(header);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2,
                IsSplitterFixed = false,
                SplitterWidth = 1,
                BackColor = Theme.Canvas
            };
            split.Panel1.BackColor = Theme.Canvas;
            split.Panel2.BackColor = Theme.Canvas;
            var adjustingSplit = false;
            split.SizeChanged += (s, e) =>
            {
                if (adjustingSplit || split.Width < 560) return;
                adjustingSplit = true;
                try
                {
                    var scale = Math.Max(1f, DeviceDpi / 96f);
                    var minimumTicket = (int)Math.Round(360 * scale);
                    var maximumTicket = (int)Math.Round(520 * scale);
                    var ticketWidth = Math.Max(minimumTicket, Math.Min(maximumTicket, (int)(split.Width * 0.38)));
                    var distance = Math.Max(250, split.Width - ticketWidth);
                    if (distance < split.Width) split.SplitterDistance = distance;
                }
                finally { adjustingSplit = false; }
            };
            Controls.Add(split);
            split.BringToFront();

            // ---- Artículos: búsqueda, categorías y rejilla ------------------------------------------------------------
            var productArea = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Lg, Theme.Space.Lg, 0) };
            productFlow = new BufferedFlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Theme.Canvas,
                Padding = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Lg)
            };
            productFlow.SizeChanged += (s, e) => ResizeProductTiles();
            productEmpty = Theme.EmptyState("No hay artículos que coincidan", "Pruebe con otra búsqueda o con otra categoría.");
            productEmpty.Visible = false;
            productArea.Controls.Add(productEmpty);
            productArea.Controls.Add(productFlow);

            searchBox = new TextBox { Font = Theme.Body, Tag = "Buscar artículo", Dock = DockStyle.Fill };
            var searchField = Theme.Field(searchBox, Theme.Dim.Field);
            searchField.Dock = DockStyle.Fill;
            var searchHost = new Panel { Dock = DockStyle.Top, Height = Theme.Dim.Field + Theme.Space.Md, BackColor = Theme.Canvas };
            searchHost.Controls.Add(searchField);
            searchField.Dock = DockStyle.Top;
            productSearchTimer = new Timer { Interval = 160 };
            productSearchTimer.Tick += (s, e) =>
            {
                productSearchTimer.Stop();
                RenderProducts();
            };
            searchBox.TextChanged += (s, e) =>
            {
                productSearchTimer.Stop();
                productSearchTimer.Start();
            };
            categoryFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Theme.Canvas,
                Margin = new Padding(0)
            };
            productArea.Controls.Add(categoryFlow);
            productArea.Controls.Add(searchHost);
            split.Panel1.Controls.Add(productArea);

            // ---- Ticket: superficie elevada a la derecha --------------------------------------------------------------
            var ticketHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Sm, Theme.Space.Lg, Theme.Space.Xl, Theme.Space.Xl) };
            var card = new CardPanel { Dock = DockStyle.Fill, Elevated = true, Padding = new Padding(Theme.Space.Lg, Theme.Space.Lg, Theme.Space.Lg, Theme.Space.Lg + 2) };
            ticketHost.Controls.Add(card);
            split.Panel2.Controls.Add(ticketHost);

            var ticketLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            ticketLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            ticketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ticketLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            ticketLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(ticketLayout);

            // Cabecera del ticket + datos del cliente
            var head = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = Theme.Label("Ticket", 14f, FontStyle.Bold);
            title.Font = Theme.Title;
            title.Dock = DockStyle.Fill;
            title.AutoSize = false;
            title.Height = Theme.Dim.Field;
            title.TextAlign = ContentAlignment.MiddleLeft;
            savedTicketsButton = Theme.Button("Guardados (0)", ButtonVariant.Secondary);
            savedTicketsButton.Height = Theme.Dim.Field;
            savedTicketsButton.Width = 150;
            savedTicketsButton.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            savedTicketsButton.Click += (s, e) => OpenSavedTicket();
            head.Controls.Add(title, 0, 0);
            head.Controls.Add(savedTicketsButton, 1, 0);

            customerStatus = Theme.Label("Cliente opcional", 9f, FontStyle.Regular, Theme.Muted);
            customerStatus.Dock = DockStyle.Fill;
            customerStatus.Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Xs);
            head.Controls.Add(customerStatus, 0, 1);
            head.SetColumnSpan(customerStatus, 2);

            dniBox = new TextBox { Font = Theme.Body, MaxLength = 8, Tag = "DNI (8 dígitos)", Dock = DockStyle.Fill };
            var dniField = Theme.Field(dniBox, Theme.Dim.Field);
            dniField.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            lookupDniButton = Theme.Button("Consultar", ButtonVariant.Secondary);
            lookupDniButton.Height = Theme.Dim.Field;
            lookupDniButton.Width = 110;
            lookupDniButton.Margin = new Padding(Theme.Space.Sm, 0, 0, Theme.Space.Sm);
            lookupDniButton.Click += async (s, e) => await LookupDniAsync(false);
            head.Controls.Add(dniField, 0, 2);
            head.Controls.Add(lookupDniButton, 1, 2);

            customerBox = new TextBox { Font = Theme.Body, MaxLength = 120, Tag = "Nombre completo", Dock = DockStyle.Fill };
            var customerField = Theme.Field(customerBox, Theme.Dim.Field);
            customerField.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            head.Controls.Add(customerField, 0, 3);
            head.SetColumnSpan(customerField, 2);

            // RF-06 / RN-14: teléfono opcional. RNF-09: el DNI y el teléfono solo se guardan con el consentimiento del cliente (Ley N.º 29733).
            phoneBox = new TextBox { Font = Theme.Body, MaxLength = 15, Tag = "Teléfono (opcional, 9 dígitos)", Dock = DockStyle.Fill };
            var phoneField = Theme.Field(phoneBox, Theme.Dim.Field);
            phoneField.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            head.Controls.Add(phoneField, 0, 4);
            head.SetColumnSpan(phoneField, 2);

            consentBox = new CheckBox
            {
                Text = "El cliente autoriza el uso de sus datos (Ley 29733)",
                Font = Theme.Caption, ForeColor = Theme.Ink, BackColor = Theme.Surface,
                AutoSize = false, Dock = DockStyle.Fill, Height = 34, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, Theme.Space.Sm)
            };
            head.Controls.Add(consentBox, 0, 5);
            head.SetColumnSpan(consentBox, 2);

            borrowedLockersLabel = Theme.Button("", ButtonVariant.Secondary);
            borrowedLockersLabel.Dock = DockStyle.Fill;
            borrowedLockersLabel.Height = 58;
            borrowedLockersLabel.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            borrowedLockersLabel.Visible = false;
            borrowedLockersLabel.TextAlign = ContentAlignment.MiddleLeft;
            borrowedLockersLabel.Padding = new Padding(Theme.Space.Md, 0, Theme.Space.Md, 0);
            borrowedLockersLabel.BackColor = Theme.GreenSoft;
            borrowedLockersLabel.ForeColor = Theme.GreenDark;
            borrowedLockersLabel.FlatAppearance.BorderColor = Theme.Primary;
            borrowedLockersLabel.Click += (s, e) => EditLoadedTicketLockers();
            head.Controls.Add(borrowedLockersLabel, 0, 6);
            head.SetColumnSpan(borrowedLockersLabel, 2);

            promoBanner = Theme.Banner("", Tone.Success);
            promoBanner.Dock = DockStyle.Fill;
            promoBanner.Margin = new Padding(0, 0, 0, Theme.Space.Sm);
            promoBanner.Visible = false;
            head.Controls.Add(promoBanner, 0, 7);
            head.SetColumnSpan(promoBanner, 2);
            ticketLayout.Controls.Add(head, 0, 0);

            dniBox.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            dniBox.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LookupDniAsync(false); } };
            dniLookupTimer = new Timer { Interval = 650 };
            dniLookupTimer.Tick += async (s, e) => { dniLookupTimer.Stop(); await LookupDniAsync(true); };
            dniBox.TextChanged += DniBox_TextChanged;
            phoneBox.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != ' ' && e.KeyChar != '+' && e.KeyChar != '-') e.Handled = true; };
            promoHintTimer = new Timer { Interval = 300 };
            promoHintTimer.Tick += PromoHintTimer_Tick;
            Disposed += (s, e) =>
            {
                promoHintVersion++;
                savedInfoVersion++;
                promoHintTimer.Stop();
                promoHintTimer.Dispose();
                dniLookupTimer.Stop();
                dniLookupTimer.Dispose();
                productSearchTimer.Stop();
                productSearchTimer.Dispose();
            };

            // Líneas del carrito
            var cartHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            cartGrid = new DataGridView { Dock = DockStyle.Fill, Margin = new Padding(0) };
            Theme.StyleGrid(cartGrid);
            cartGrid.ColumnHeadersVisible = false;
            cartGrid.RowTemplate.Height = 60;
            cartGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            cartGrid.DefaultCellStyle.SelectionBackColor = Theme.Surface;
            cartGrid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Product", HeaderText = "Artículo", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 90 });
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Minus", Width = 38, DefaultCellStyle = { Padding = Padding.Empty } });
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Qty", HeaderText = "Cant.", Width = 40, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, Font = Theme.Emphasis, Padding = new Padding(1, 0, 1, 0) } });
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Plus", Width = 38, DefaultCellStyle = { Padding = Padding.Empty } });
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "Total", Width = 108, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, Font = Theme.Subhead } });
            cartGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Remove", Width = 38, DefaultCellStyle = { Padding = Padding.Empty } });
            cartGrid.CellClick += CartGrid_CellClick;
            cartGrid.CellPainting += CartGrid_CellPainting;
            cartGrid.CellMouseEnter += (s, e) => SetCartHover(new Point(e.ColumnIndex, e.RowIndex));
            cartGrid.CellMouseLeave += (s, e) => SetCartHover(new Point(-1, -1));
            cartGrid.CellMouseDown += (s, e) => { pressedCell = new Point(e.ColumnIndex, e.RowIndex); InvalidateCartCell(pressedCell); cartGrid.Update(); };
            cartGrid.CellMouseUp += (s, e) => { var old = pressedCell; pressedCell = new Point(-1, -1); InvalidateCartCell(old); };
            cartEmpty = Theme.EmptyState("El ticket está vacío", "Toque un artículo para agregarlo.");
            cartHost.Controls.Add(cartEmpty);
            cartHost.Controls.Add(cartGrid);
            ticketLayout.Controls.Add(cartHost, 0, 1);

            // Pie: total y acciones
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0, Theme.Space.Sm, 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var totalRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, Theme.Space.Md), Padding = new Padding(0) };
            totalRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            totalRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            itemCountLabel = Theme.Label("Ticket vacío", 10.5f, FontStyle.Regular, Theme.Muted);
            itemCountLabel.Dock = DockStyle.Fill;
            itemCountLabel.AutoSize = false;
            itemCountLabel.TextAlign = ContentAlignment.MiddleLeft;
            totalLabel = Theme.Label(Theme.Money(0), 26f, FontStyle.Bold);
            totalLabel.Font = Theme.Display;
            totalLabel.TextAlign = ContentAlignment.MiddleRight;
            totalLabel.Dock = DockStyle.Fill;
            totalRow.Controls.Add(itemCountLabel, 0, 0);
            totalRow.Controls.Add(totalLabel, 1, 0);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0), Padding = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var clear = Theme.Button("Limpiar", ButtonVariant.Ghost);
            clear.Width = 88;
            clear.Dock = DockStyle.Fill;
            clear.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            clear.Click += (s, e) => ClearCurrentTicket();
            saveButton = Theme.Button("Guardar", ButtonVariant.Secondary);
            saveButton.Width = 104;
            saveButton.Dock = DockStyle.Fill;
            saveButton.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            saveButton.Click += (s, e) => SaveCurrentTicket();
            chargeButton = Theme.Button("Cobrar", ButtonVariant.Primary);
            chargeButton.Dock = DockStyle.Fill;
            chargeButton.Height = Theme.Dim.Control + Theme.Space.Sm;
            chargeButton.Margin = new Padding(0);
            chargeButton.Font = Theme.Subhead;
            chargeButton.Click += ChargeButton_Click;
            buttons.Controls.Add(clear, 0, 0);
            buttons.Controls.Add(saveButton, 1, 0);
            buttons.Controls.Add(chargeButton, 2, 0);

            var divider = Theme.Separator();
            divider.Dock = DockStyle.Top;
            footer.Controls.Add(divider, 0, 0);
            footer.Controls.Add(totalRow, 0, 1);
            footer.Controls.Add(buttons, 0, 2);
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 1f + Theme.Space.Sm));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ticketLayout.Controls.Add(footer, 0, 2);

            RestoreIdleDraft();
            RefreshView();
        }

        // Carrito en curso conservado en memoria cuando la sesión se cierra por inactividad (RNF-03). Se devuelve solo al MISMO usuario
        // en su siguiente ingreso; al cerrar la sesión a mano el carrito se descarta, como siempre.
        private sealed class IdleDraft
        {
            public string UserId, Customer, Dni, Phone, CustomerSource, SavedTicketId;
            public bool Consent;
            public List<ReceiptLine> Lines;
            public List<int> Lockers;
        }

        private static IdleDraft idleDraft;

        // Cierre de sesión manual: no se arrastra un borrador de una caída anterior.
        public static void DiscardIdleDraft() { idleDraft = null; }

        public void StashIdleDraft()
        {
            var user = AppSession.CurrentUser;
            if (user == null || cart.Count == 0) { idleDraft = null; return; }
            idleDraft = new IdleDraft
            {
                UserId = user.Id,
                Customer = customerBox.Text,
                Dni = dniBox.Text,
                Phone = phoneBox.Text,
                Consent = consentBox.Checked,
                CustomerSource = customerSource,
                SavedTicketId = loadedSavedTicketId,
                Lockers = loadedLockerNumbers.ToList(),
                Lines = cart.Select(x => new ReceiptLine { ProductId = x.ProductId, OptionId = x.OptionId, Name = x.Name, Comment = x.Comment, UnitPrice = x.UnitPrice, Quantity = x.Quantity }).ToList()
            };
        }

        private void RestoreIdleDraft()
        {
            var draft = idleDraft;
            idleDraft = null;   // se usa una sola vez; si entra otra persona, se descarta
            if (draft == null || AppSession.CurrentUser == null || AppSession.CurrentUser.Id != draft.UserId) return;
            cart.AddRange(draft.Lines);
            customerBox.Text = draft.Customer;
            lastAutoLookup = Validation.IsValidDni(draft.Dni) ? draft.Dni : null;   // el DNI restaurado no dispara la consulta automática
            dniBox.Text = draft.Dni;
            phoneBox.Text = draft.Phone ?? "";
            consentBox.Checked = draft.Consent;
            customerSource = draft.CustomerSource;
            loadedSavedTicketId = draft.SavedTicketId;
            loadedLockerNumbers = draft.Lockers;
        }

        public void RefreshView()
        {
            availableProducts = AppData.Products.Where(x => x.Active).ToList();
            var names = availableProducts.Select(x => x.Category).Distinct().OrderBy(x => x).ToList();
            if (selectedCategory == null && categoryFlow.Controls.Count == 0 && names.Count > 0) selectedCategory = names[0];
            else if (selectedCategory != null && !names.Contains(selectedCategory)) selectedCategory = names.Count > 0 ? names[0] : null;

            categoryFlow.SuspendLayout();
            var previous = categoryFlow.Controls.Cast<Control>().ToArray();
            categoryFlow.Controls.Clear();
            foreach (var control in previous) control.Dispose();
            AddCategoryChip("Todos", null);
            foreach (var name in names) AddCategoryChip(name, name);
            categoryFlow.ResumeLayout();
            RenderProducts();
            RenderCart();
            UpdateSavedTicketButton();
            RefreshSavedTicketsInfo();
        }

        private void AddCategoryChip(string text, string category)
        {
            var chip = new SegmentChip(text)
            {
                Tag = category,
                Active = category == selectedCategory,
                Margin = new Padding(0, 0, Theme.Space.Sm, Theme.Space.Sm)
            };
            chip.Click += (s, e) =>
            {
                selectedCategory = (string)((Control)s).Tag;
                foreach (SegmentChip other in categoryFlow.Controls.OfType<SegmentChip>()) other.Active = (string)other.Tag == selectedCategory;
                RenderProducts();
            };
            categoryFlow.Controls.Add(chip);
        }

        private void RenderProducts()
        {
            if (productFlow == null) return;
            var query = availableProducts.AsEnumerable();
            if (selectedCategory != null) query = query.Where(x => x.Category == selectedCategory);
            var term = (searchBox.Text ?? "").Trim();
            if (term.Length > 0) query = query.Where(x => x.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0);

            productFlow.SuspendLayout();
            var previousControls = productFlow.Controls.Cast<Control>().ToArray();
            productFlow.Controls.Clear();
            foreach (var control in previousControls) control.Dispose();
            foreach (var product in query.OrderBy(x => x.Name))
            {
                var tile = new ProductTile(product);
                tile.ProductSelected += (s, e) => AddProduct(e);
                productFlow.Controls.Add(tile);
            }
            ResizeProductTiles();
            var none = productFlow.Controls.Count == 0;
            productEmpty.Visible = none;
            productFlow.Visible = !none;
            productFlow.ResumeLayout();
        }

        private void AddProduct(Product product)
        {
            if (ProductOptionCatalog.HasOptions(product))
            {
                using (var dialog = new ProductOptionsDialog(product))
                {
                    if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.SelectedOption == null) return;
                    var option = dialog.SelectedOption;
                    var lineName = product.Name + " · " + option.Name;
                    var optionLine = cart.FirstOrDefault(x =>
                        x.ProductId == product.Id &&
                        x.OptionId == option.Id &&
                        x.Name == lineName &&
                        x.UnitPrice == option.Price &&
                        string.Equals(x.Comment ?? "", dialog.SelectedComment ?? "", StringComparison.OrdinalIgnoreCase));
                    if (optionLine == null)
                    {
                        cart.Add(new ReceiptLine
                        {
                            ProductId = product.Id,
                            OptionId = option.Id,
                            Name = lineName,
                            Comment = dialog.SelectedComment,
                            UnitPrice = option.Price,
                            Quantity = dialog.SelectedQuantity
                        });
                    }
                    else
                    {
                        var newQuantity = optionLine.Quantity + dialog.SelectedQuantity;
                        optionLine.Quantity = Math.Min(999, newQuantity);
                        if (newQuantity > 999)
                            Theme.Message(FindForm(), Tone.Warning, "La cantidad máxima por artículo es 999.", "Validación del ticket");
                    }
                }
                RenderCart();
                return;
            }

            var line = cart.FirstOrDefault(x => x.ProductId == product.Id && string.IsNullOrWhiteSpace(x.Comment));
            if (line == null)
            {
                cart.Add(new ReceiptLine { ProductId = product.Id, Name = product.Name, UnitPrice = product.Price, Quantity = 1 });
            }
            else if (line.Quantity < 999) line.Quantity++;
            else Theme.Message(FindForm(), Tone.Warning, "La cantidad máxima por artículo es 999.", "Validación del ticket");
            RenderCart();
        }

        private void RenderCart()
        {
            cartGrid.SuspendLayout();
            cartGrid.Rows.Clear();
            foreach (var line in cart)
            {
                var row = cartGrid.Rows.Add(line.Name, "", line.Quantity, "", Theme.Money(line.LineTotal), "");
                cartGrid.Rows[row].Tag = line;
            }
            var count = cart.Sum(x => x.Quantity);
            var total = cart.Sum(x => x.LineTotal);
            cartEmpty.Visible = cart.Count == 0;
            cartGrid.Visible = cart.Count > 0;
            itemCountLabel.Text = count == 0 ? "Ticket vacío" : "Total · " + count + (count == 1 ? " artículo" : " artículos");
            totalLabel.Text = Theme.Money(total);
            chargeButton.Text = count == 0 ? "Cobrar" : "Cobrar  " + Theme.Money(total);
            chargeButton.Enabled = count > 0;
            saveButton.Enabled = count > 0;
            saveButton.Text = string.IsNullOrWhiteSpace(loadedSavedTicketId) ? "Guardar" : "Actualizar";
            UpdateBorrowedLockersSummary();   // solo usa datos en memoria; los tickets guardados se refrescan en RefreshSavedTicketsInfo
            UpdatePromotionHint();
            cartGrid.ResumeLayout();
        }

        // Aviso visible de promoción (solo informativo: el cobro la vuelve a calcular en ChargeButton_Click y en AppData.AddReceipt).
        // Nunca consulta MySQL en el hilo de la UI: el cálculo se difiere ~300 ms (se reinicia en cada cambio) y corre en segundo plano.
        private void UpdatePromotionHint()
        {
            if (promoBanner == null || promoHintTimer == null) return;
            promoHintVersion++;
            promoHintTimer.Stop();
            if (cart.Count == 0)
            {
                promoBanner.Visible = false;
                return;
            }
            promoHintTimer.Start();
        }

        private void PromoHintTimer_Tick(object sender, EventArgs e)
        {
            promoHintTimer.Stop();
            if (cart.Count == 0) { promoBanner.Visible = false; return; }
            var version = ++promoHintVersion;
            var lines = cart.Select(x => new ReceiptLine { ProductId = x.ProductId, OptionId = x.OptionId, Name = x.Name, Comment = x.Comment, UnitPrice = x.UnitPrice, Quantity = x.Quantity }).ToList();
            var dni = dniBox.Text;
            System.Threading.Tasks.Task.Run(() =>
            {
                PromocionResultado promotion = null;
                Exception error = null;
                try { promotion = AppData.CalculatePromotion(dni, lines); }
                catch (Exception ex) { error = ex; }
                if (error != null && !promoHintFailed)
                {
                    promoHintFailed = true;   // se registra una sola vez hasta que vuelva a funcionar
                    try { AppData.LogError(error, "Vista previa de promoción"); } catch { }
                }
                else if (error == null) promoHintFailed = false;
                if (!IsHandleCreated || IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || version != promoHintVersion) return;
                        if (promotion != null && promotion.Aplica)
                        {
                            promoBanner.Text = "Promoción: " + promotion.Descripcion + ". Se descontarán " + Theme.Money(promotion.Descuento) + " al cobrar.";
                            promoBanner.Visible = true;
                        }
                        else promoBanner.Visible = false;
                    }));
                }
                catch (InvalidOperationException) { }   // incluye ObjectDisposedException (control cerrado durante el cálculo)
            });
        }

        // ---- Dibujo del carrito: nombre y precio en dos líneas, botones −/+/× sin bordes negros --------------------
        private static bool IsStepper(string name) { return name == "Minus" || name == "Plus" || name == "Remove"; }

        private void SetCartHover(Point cell)
        {
            var old = hoverCell;
            hoverCell = cell;
            InvalidateCartCell(old);
            InvalidateCartCell(cell);
            cartGrid.Cursor = cell.X >= 0 && cell.Y >= 0 && IsStepper(cartGrid.Columns[cell.X].Name) ? Cursors.Hand : Cursors.Default;
        }

        private void InvalidateCartCell(Point cell)
        {
            if (cell.X >= 0 && cell.Y >= 0 && cell.Y < cartGrid.RowCount && cell.X < cartGrid.ColumnCount) cartGrid.InvalidateCell(cell.X, cell.Y);
        }

        private void CartGrid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var line = cartGrid.Rows[e.RowIndex].Tag as ReceiptLine;
            if (line == null) return;
            var column = cartGrid.Columns[e.ColumnIndex].Name;
            if (column != "Product" && !IsStepper(column)) return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
            var g = e.Graphics;
            var bounds = e.CellBounds;
            if (column == "Product")
            {
                var left = bounds.X + Theme.Space.Sm;
                var width = Math.Max(10, bounds.Width - Theme.Space.Sm * 2);
                var nameHeight = LineNameFont.Height;
                var detailHeight = Theme.Caption.Height;
                var top = bounds.Y + (bounds.Height - nameHeight - detailHeight - 2) / 2;
                var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, line.Name, LineNameFont, new Rectangle(left, top, width, nameHeight), Theme.Ink, flags);
                var detail = Theme.Money(line.UnitPrice) + " c/u" + (string.IsNullOrWhiteSpace(line.Comment) ? "" : "  •  " + line.Comment);
                TextRenderer.DrawText(g, detail, Theme.Caption, new Rectangle(left, top + nameHeight + 2, width, detailHeight), Theme.Muted, flags);
                e.Handled = true;
                return;
            }

            var size = 30;
            var box = new Rectangle(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
            var cell = new Point(e.ColumnIndex, e.RowIndex);
            var hover = hoverCell == cell;
            var pressed = pressedCell == cell;
            var remove = column == "Remove";
            var fill = pressed ? (remove ? Theme.DangerSoft : Theme.SoftPressed) : hover ? (remove ? Theme.DangerSoft : Theme.GreenSoft) : Theme.SurfaceSunken;
            var ink = remove && (hover || pressed) ? Theme.Danger : Theme.Ink;
            var previous = g.SmoothingMode;   // Graphics compartido por todas las celdas: se restaura al terminar
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Gfx.Round(box, Theme.Radius.Control))
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var cx = box.X + size / 2; var cy = box.Y + size / 2; const int r = 5;
                if (column == "Minus") g.DrawLine(pen, cx - r, cy, cx + r, cy);
                else if (column == "Plus") { g.DrawLine(pen, cx - r, cy, cx + r, cy); g.DrawLine(pen, cx, cy - r, cx, cy + r); }
                else { g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4); g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4); }
            }
            g.SmoothingMode = previous;
            e.Handled = true;
        }

        private void ResizeProductTiles()
        {
            if (productFlow == null || productFlow.ClientSize.Width <= 0) return;
            var available = productFlow.ClientSize.Width - productFlow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
            if (available <= 0) return;
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var preferred = Math.Max(150, (int)(190 * scale));
            var min = Math.Max(125, (int)(140 * scale));
            var max = Math.Max(190, (int)(240 * scale));
            var gap = Theme.Space.Md;
            var columns = Math.Max(1, available / preferred);
            if (columns == 1 && available >= (min * 2 + gap * 2)) columns = 2;
            var width = (available - columns * gap) / columns;
            width = Math.Max(Math.Min(width, max), Math.Min(min, available - gap));
            foreach (ProductTile tile in productFlow.Controls.OfType<ProductTile>()) tile.SetResponsiveWidth(width, scale);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (savedInfoPending) { savedInfoPending = false; RefreshSavedTicketsInfo(); }
        }

        private void UpdateSavedTicketButton()
        {
            if (savedTicketsButton == null) return;
            var count = savedTicketCount;
            savedTicketsButton.Text = "Guardados (" + count + ")";
            savedTicketsButton.ForeColor = count > 0 ? Theme.GreenDark : Theme.Muted;
        }

        // Número de tickets guardados y datos de la llave prestada: no dependen del carrito, así que se refrescan solo cuando cambian
        // los datos (carga, RefreshView/DataChanged, guardar/actualizar/borrar/cobrar). Nunca consulta MySQL en el hilo de la UI:
        // si falla se conserva el último valor mostrado y se registra el error una sola vez hasta que vuelva a funcionar.
        private void RefreshSavedTicketsInfo()
        {
            if (savedTicketsButton == null) return;
            var version = ++savedInfoVersion;
            System.Threading.Tasks.Task.Run(() =>
            {
                List<SavedTicket> tickets = null;
                Exception error = null;
                try { tickets = AppData.SavedTickets; }
                catch (Exception ex) { error = ex; }
                if (error != null)
                {
                    if (!savedInfoFailed)
                    {
                        savedInfoFailed = true;   // se registra una sola vez hasta que vuelva a funcionar
                        try { AppData.LogError(error, "Tickets guardados"); } catch { }
                    }
                    return;
                }
                savedInfoFailed = false;
                if (IsDisposed) return;
                if (!IsHandleCreated) { savedInfoPending = true; return; }   // la carga inicial termina antes de que exista el handle: OnHandleCreated repite
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || version != savedInfoVersion) return;
                        savedTicketCount = tickets.Count;
                        savedTicketsById = tickets.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
                        UpdateSavedTicketButton();
                        UpdateBorrowedLockersSummary();
                    }));
                }
                catch (InvalidOperationException) { }   // incluye ObjectDisposedException (control cerrado durante la consulta)
            });
        }

        private void DniBox_TextChanged(object sender, EventArgs e)
        {
            var normalized = Validation.NormalizeDni(dniBox.Text);
            if (dniBox.Text != normalized)
            {
                var position = dniBox.SelectionStart;
                dniBox.Text = normalized;
                dniBox.SelectionStart = Math.Min(position, dniBox.TextLength);
                return;
            }
            dniLookupTimer.Stop();
            if (normalized.Length == 8 && normalized != lastAutoLookup)
            {
                customerStatus.Text = "DNI válido; preparando consulta…";
                customerStatus.ForeColor = Theme.Muted;
                dniLookupTimer.Start();
            }
            else if (normalized.Length > 0 && normalized.Length < 8)
            {
                customerStatus.Text = "Faltan " + (8 - normalized.Length) + " dígitos.";
                customerStatus.ForeColor = Theme.Warning;
            }
            else if (normalized.Length == 0)
            {
                customerStatus.Text = "Cliente opcional";
                customerStatus.ForeColor = Theme.Muted;
                lastAutoLookup = null;
            }
            if (normalized.Length == 8 || promoBanner.Visible) UpdatePromotionHint();
        }

        private async System.Threading.Tasks.Task LookupDniAsync(bool automatic)
        {
            if (lookupInProgress) return;
            var dni = Validation.NormalizeDni(dniBox.Text);
            if (!Validation.IsValidDni(dni))
            {
                customerStatus.Text = "Ingrese un DNI válido de 8 dígitos.";
                customerStatus.ForeColor = Theme.Danger;
                if (!automatic) Theme.Message(FindForm(), Tone.Warning, customerStatus.Text, "Validación de DNI");
                return;
            }
            lookupInProgress = true;
            lastAutoLookup = dni;
            lookupDniButton.Enabled = false;
            lookupDniButton.Text = "Buscando…";
            customerStatus.Text = "Consultando de forma segura…";
            customerStatus.ForeColor = Theme.Muted;
            try
            {
                var result = await DniLookupService.LookupAsync(dni);
                if (result.Success)
                {
                    customerBox.Text = result.FullName;
                    customerSource = result.Source == "Registro local" ? null : result.Source;
                    if (result.DataConsent)
                    {
                        // Cliente registrado que ya autorizó el uso de sus datos: se recupera su teléfono y su consentimiento.
                        consentBox.Checked = true;
                        if (string.IsNullOrWhiteSpace(phoneBox.Text)) phoneBox.Text = result.Phone ?? "";
                    }
                    customerStatus.Text = "✓ " + result.Source;
                    customerStatus.ForeColor = Theme.Success;
                    UpdateBorrowedLockersSummary();
                }
                else
                {
                    customerStatus.Text = result.Message;
                    customerStatus.ForeColor = Theme.Warning;
                    if (!automatic && result.Message.IndexOf("desactivada", StringComparison.OrdinalIgnoreCase) < 0)
                        Theme.Message(FindForm(), Tone.Info, result.Message, "Consulta de DNI");
                }
            }
            finally
            {
                lookupInProgress = false;
                lookupDniButton.Enabled = true;
                lookupDniButton.Text = "Consultar";
            }
        }

        private bool ValidateCustomer()
        {
            var dni = Validation.NormalizeDni(dniBox.Text);
            var name = customerBox.Text.Trim();
            if (dni.Length > 0 && !Validation.IsValidDni(dni))
            {
                Theme.Message(FindForm(), Tone.Warning, "El DNI debe tener exactamente 8 dígitos.", "Datos del cliente");
                dniBox.Focus();
                return false;
            }
            if (dni.Length > 0 && !Validation.IsValidPersonName(name))
            {
                Theme.Message(FindForm(), Tone.Warning, "Ingrese o consulte el nombre completo correspondiente al DNI.", "Datos del cliente");
                customerBox.Focus();
                return false;
            }
            if (name.Length > 0 && !Validation.IsValidPersonName(name))
            {
                Theme.Message(FindForm(), Tone.Warning, "El nombre del cliente debe tener entre 3 y 120 caracteres.", "Datos del cliente");
                customerBox.Focus();
                return false;
            }
            // RF-06 / RN-14: teléfono opcional, celular peruano de 9 dígitos.
            var phone = Validation.NormalizePhone(phoneBox.Text);
            if (!Validation.IsValidPhone(phoneBox.Text))
            {
                Theme.Message(FindForm(), Tone.Warning, "El teléfono debe ser un celular de 9 dígitos que empiece en 9. Si el cliente no desea darlo, deje el campo vacío.", "Datos del cliente");
                phoneBox.Focus();
                return false;
            }
            if (phone.Length > 0 && dni.Length == 0)
            {
                Theme.Message(FindForm(), Tone.Warning, "Para registrar el teléfono ingrese también el DNI del cliente. Si no lo tiene, deje el teléfono vacío.", "Datos del cliente");
                dniBox.Focus();
                return false;
            }
            // RNF-09 (Ley N.º 29733): sin consentimiento solo se registra el nombre. RN-11 sigue vigente: si la venta exige DNI, se pide la autorización.
            if ((dni.Length > 0 || phone.Length > 0) && !consentBox.Checked)
            {
                if (cart.Sum(x => x.LineTotal) > AppData.DniObligatorioDesde)
                {
                    Theme.Message(FindForm(), Tone.Warning,
                        "Las ventas mayores a " + Theme.Money(AppData.DniObligatorioDesde) + " requieren el DNI del cliente. Pídale su autorización para usar sus datos " +
                        "(Ley N.º 29733) y marque la casilla antes de continuar.", "Consentimiento de datos");
                    consentBox.Focus();
                    return false;
                }
                var answer = MessageBox.Show(FindForm(),
                    "Para guardar el DNI o el teléfono se necesita la autorización del cliente (Ley N.º 29733).\n\n" +
                    "¿Desea continuar solo con el nombre? El DNI y el teléfono se borrarán de este ticket.\n" +
                    "Si el cliente autoriza, elija No y marque la casilla de consentimiento.",
                    "Consentimiento de datos", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) { consentBox.Focus(); return false; }
                lastAutoLookup = null;
                dniBox.Clear();
                phoneBox.Clear();
                dni = "";
                phone = "";
            }
            if (dni.Length == 8 && name.Length > 0 && consentBox.Checked)
            {
                try
                {
                    var existing = AppData.FindCustomer(dni);
                    if (existing == null || !existing.DataConsent || (existing.Phone ?? "") != phone ||
                        !string.Equals(existing.FullName, Validation.NormalizeName(name), StringComparison.OrdinalIgnoreCase))
                        AppData.SaveCustomer(dni, name,
                            !string.IsNullOrWhiteSpace(customerSource) ? customerSource : existing != null ? existing.Source : "Ingreso manual", phone, true);
                }
                catch (ArgumentException ex)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "Datos del cliente");
                    return false;
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Guardar cliente (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Datos del cliente");
                    return false;
                }
            }
            return true;
        }

        private void ClearCurrentTicket()
        {
            cart.Clear();
            customerBox.Clear();
            dniBox.Clear();
            phoneBox.Clear();
            consentBox.Checked = false;
            customerSource = null;
            lastAutoLookup = null;
            customerStatus.Text = "Cliente opcional";
            customerStatus.ForeColor = Theme.Muted;
            loadedSavedTicketId = null;
            loadedLockerNumbers.Clear();
            RenderCart();
        }

        private void SaveCurrentTicket()
        {
            if (cart.Count == 0) return;
            if (!ValidateCustomer()) return;
            var existing = string.IsNullOrWhiteSpace(loadedSavedTicketId)
                ? null
                : AppData.SavedTickets.FirstOrDefault(x => x.Id == loadedSavedTicketId);
            var suggested = existing != null
                ? existing.Name
                : string.IsNullOrWhiteSpace(customerBox.Text) ? "Ticket " + DateTime.Now.ToString("HH:mm") : customerBox.Text.Trim();
            var lockers = existing == null ? loadedLockerNumbers : AppData.GetTicketLockers(existing);
            int inferredAdults;
            int inferredChildren;
            List<string> unmatchedProducts;
            OccupancyCalculator.Count(cart, out inferredAdults, out inferredChildren, out unmatchedProducts);
            if (unmatchedProducts.Count > 0)
                Theme.Message(FindForm(), Tone.Warning,
                    "No se pudo calcular cuántas personas incluyen estos artículos: " + string.Join(", ", unmatchedProducts) +
                    ".\nRevise y ajuste manualmente la cantidad de adultos y niños antes de guardar.",
                    "Conteo de ocupación");
            var adults = existing == null ? inferredAdults : Math.Max(AppData.GetTicketAdultCount(existing), inferredAdults);
            var children = existing == null ? inferredChildren : Math.Max(AppData.GetTicketChildCount(existing), inferredChildren);
            using (var dialog = new SaveTicketDialog(suggested, lockers, adults, children, existing == null ? null : existing.Id))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    if (existing == null)
                        AppData.SaveTicket(cart, customerBox.Text, dniBox.Text, dialog.TicketName, dialog.LockerNumbers, dialog.AdultCount, dialog.ChildCount);
                    else
                        AppData.UpdateSavedTicket(existing.Id, cart, customerBox.Text, dniBox.Text, dialog.TicketName, dialog.LockerNumbers, dialog.AdultCount, dialog.ChildCount);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "Guardar ticket");
                    return;
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Guardar ticket (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Guardar ticket");
                    return;
                }
            }
            ClearCurrentTicket();
            RefreshSavedTicketsInfo();
        }

        private void OpenSavedTicket()
        {
            using (var dialog = new SavedTicketsDialog())
            {
                var dialogResult = dialog.ShowDialog(FindForm());
                RefreshSavedTicketsInfo();   // el diálogo puede haber eliminado tickets o cambiado llaves
                if (dialogResult != DialogResult.OK || dialog.SelectedTicket == null) return;
                if (cart.Count > 0 && MessageBox.Show("El ticket actual será reemplazado. ¿Continuar?", "Abrir ticket guardado", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                cart.Clear();
                foreach (var line in dialog.SelectedTicket.Lines)
                    cart.Add(new ReceiptLine { ProductId = line.ProductId, OptionId = line.OptionId, Name = line.Name, Comment = line.Comment, UnitPrice = line.UnitPrice, Quantity = line.Quantity });
                customerBox.Text = dialog.SelectedTicket.CustomerName == "Público general" ? "" : dialog.SelectedTicket.CustomerName;
                dniBox.Text = dialog.SelectedTicket.CustomerDni ?? "";
                // RF-06: el teléfono y el consentimiento viven en el registro de clientes (por DNI), no en el ticket guardado.
                phoneBox.Clear();
                consentBox.Checked = false;
                customerSource = null;
                if (Validation.IsValidDni(dniBox.Text))
                {
                    try
                    {
                        var registered = AppData.FindCustomer(dniBox.Text);
                        if (registered != null && registered.DataConsent)
                        {
                            consentBox.Checked = true;
                            phoneBox.Text = registered.Phone ?? "";
                        }
                    }
                    catch (MySqlConnector.MySqlException ex) { AppData.LogError(ex, "Abrir ticket guardado: cliente"); }
                }
                lastAutoLookup = Validation.IsValidDni(dniBox.Text) ? dniBox.Text : null;
                customerStatus.Text = Validation.IsValidDni(dniBox.Text) ? "✓ Ticket recuperado" : "Cliente opcional";
                customerStatus.ForeColor = Validation.IsValidDni(dniBox.Text) ? Theme.Success : Theme.Muted;
                loadedLockerNumbers = AppData.GetTicketLockers(dialog.SelectedTicket);
                loadedSavedTicketId = dialog.SelectedTicket.Id;
                savedTicketsById[dialog.SelectedTicket.Id] = dialog.SelectedTicket;   // para mostrar adultos/niños sin esperar la consulta
                RenderCart();
            }
        }

        private void UpdateBorrowedLockersSummary()
        {
            if (string.IsNullOrWhiteSpace(loadedSavedTicketId))
            {
                borrowedLockersLabel.Text = "";
                borrowedLockersLabel.Visible = false;
                return;
            }
            var displayName = string.IsNullOrWhiteSpace(customerBox.Text) ? "Público general" : customerBox.Text.Trim();
            SavedTicket ticket;
            savedTicketsById.TryGetValue(loadedSavedTicketId, out ticket);
            var people = ticket == null ? "" : "   •   Adultos: " + AppData.GetTicketAdultCount(ticket) + "   Niños: " + AppData.GetTicketChildCount(ticket) +
                                               "   •   En el local: " + SavedTicketsDialog.ElapsedText(ticket.CreatedAt);   // RF-20
            borrowedLockersLabel.Text = "Cliente: " + displayName + people + "\r\nLlaves: " + (loadedLockerNumbers.Count == 0 ? "Ninguna" : string.Join(", ", loadedLockerNumbers)) + "   • Editar";
            borrowedLockersLabel.Visible = true;
        }

        private void EditLoadedTicketLockers()
        {
            if (string.IsNullOrWhiteSpace(loadedSavedTicketId)) return;
            var ticket = AppData.SavedTickets.FirstOrDefault(x => x.Id == loadedSavedTicketId);
            if (ticket == null)
            {
                Theme.Message(FindForm(), Tone.Info, "El ticket guardado ya no existe.", "Editar llaves");
                ClearCurrentTicket();
                return;
            }

            var occupied = AppData.SavedTickets
                .Where(x => x.Id != ticket.Id)
                .SelectMany(AppData.GetTicketLockers)
                .Distinct()
                .ToList();
            using (var dialog = new LockerSelectionDialog(occupied, AppData.GetTicketLockers(ticket)))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    AppData.UpdateSavedTicketLockers(ticket.Id, dialog.SelectedLockers);
                    loadedLockerNumbers = dialog.SelectedLockers.ToList();
                    UpdateBorrowedLockersSummary();
                    RefreshSavedTicketsInfo();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "Editar llaves");
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Editar llaves (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Editar llaves");
                }
            }
        }

        private void CartGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var line = cartGrid.Rows[e.RowIndex].Tag as ReceiptLine;
            if (line == null) return;
            var name = cartGrid.Columns[e.ColumnIndex].Name;
            if (!IsStepper(name)) return;
            if (name == "Minus") { line.Quantity--; if (line.Quantity <= 0) cart.Remove(line); }
            else if (name == "Plus" && line.Quantity < 999) line.Quantity++;
            else if (name == "Remove") cart.Remove(line);
            hoverCell = new Point(-1, -1);
            RenderCart();
        }

        private void ChargeButton_Click(object sender, EventArgs e)
        {
            if (cart.Count == 0) return;
            if (!ValidateCustomer()) return;
            if (AppData.OpenShift == null)
            {
                Theme.Message(FindForm(), Tone.Warning, "Debe abrir un turno antes de cobrar una venta.", "Turno requerido");
                return;
            }
            var saleTotal = cart.Sum(x => x.LineTotal);
            if (saleTotal == 0m && MessageBox.Show("Este ticket tiene total S/ 0.00. ¿Registrar como cortesía?", "Venta sin costo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            using (var reminder = new RentalReminderDialog())
                if (reminder.ShowDialog(FindForm()) != DialogResult.OK) return;
            PromocionResultado promotion;
            try
            {
                promotion = AppData.CalculatePromotion(dniBox.Text, cart);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Calcular promoción (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo cobrar");
                return;
            }
            using (var dialog = new CheckoutDialog(saleTotal, promotion, dniBox.Text))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                Receipt receipt;
                try
                {
                    chargeButton.Enabled = false;
                    // El ticket guardado (y sus llaves) se libera dentro de la misma transacción del cobro.
                    receipt = AppData.AddReceipt(cart, customerBox.Text, dniBox.Text, dialog.PaymentMethod, dialog.AmountReceived, dialog.CashAmount, dialog.YapeAmount, dialog.Discount, dialog.PromotionId,
                        string.IsNullOrWhiteSpace(loadedSavedTicketId) ? null : loadedSavedTicketId);
                }
                catch (AppData.SavedTicketUnavailableException)
                {
                    // Otra caja ya cobró o eliminó este ticket: sin esto cada intento repetiría el mismo error.
                    chargeButton.Enabled = true;
                    var clear = MessageBox.Show(FindForm(), "Este ticket ya se cobró o se eliminó en otra caja. ¿Desea vaciar el carrito?",
                        "No se pudo cobrar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                    if (clear) ClearCurrentTicket();
                    else
                    {
                        // Se desvincula: el carrito puede cobrarse como una venta nueva si la cajera lo decide.
                        loadedSavedTicketId = null;
                        loadedLockerNumbers.Clear();
                        RenderCart();
                    }
                    RefreshSavedTicketsInfo();
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo cobrar");
                    RenderCart();
                    RefreshSavedTicketsInfo();
                    return;
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Registrar cobro (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo cobrar");
                    RenderCart();
                    return;
                }
                catch (Exception ex)
                {
                    AppData.LogError(ex, "Registrar cobro");
                    Theme.Message(FindForm(), Tone.Danger, "No se pudo registrar el cobro. Revise que la carpeta de datos tenga espacio y permiso de escritura.", "No se pudo cobrar");
                    RenderCart();
                    return;
                }
                var change = dialog.PaymentMethod == "Efectivo" ? Math.Max(0m, dialog.AmountReceived - receipt.Total) : 0m;
                var hadSavedTicket = !string.IsNullOrWhiteSpace(loadedSavedTicketId);
                ClearCurrentTicket();
                if (hadSavedTicket) RefreshSavedTicketsInfo();
                // RF-12 / RNF-07: impresión automática solo si "Imprimir recibo al cobrar" está activo en esta PC.
                // El cobro ya está registrado: un fallo de impresión solo se avisa y se registra en errors.log.
                ReceiptPrinting.PrintAfterSale(FindForm(), receipt);
                using (var complete = new SaleCompleteDialog(receipt, change)) complete.ShowDialog(FindForm());
            }
        }
    }

    // Segmento / chip de selección (categorías y medio de pago). Activo = fondo verde suave, borde Primary y texto GreenDark
    // (con marca ✓ si ShowCheck), de modo que se distingue sin depender solo del color.
    internal sealed class SegmentChip : Control
    {
        private bool active, hover, pressed;

        public SegmentChip(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Text = text;
            Font = Theme.ButtonFont;
            Height = Theme.Dim.Field;
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = GetPreferredSize(Size.Empty);
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }
        public bool ShowCheck { get; set; }
        public int CornerRadius { get; set; } = Theme.Radius.Pill;

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text ?? "", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Size(text.Width + Theme.Space.Xl + (ShowCheck ? 18 : 0), Theme.Dim.Field);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var fill = pressed ? Theme.SoftPressed : active ? Theme.GreenSoft : hover ? Theme.SurfaceSunken : Theme.Surface;
            using (var path = Gfx.Round(rect, CornerRadius))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(active ? Theme.Primary : Theme.BorderStrong, active ? 2f : 1f)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                using (var ring = Gfx.Round(new Rectangle(3, 3, Width - 7, Height - 7), Math.Max(0, CornerRadius - 3)))
                using (var pen = new Pen(Color.FromArgb(160, Theme.Primary), 1f) { DashStyle = DashStyle.Dot }) g.DrawPath(pen, ring);
            var textColor = active ? Theme.GreenDark : Theme.Ink;
            var text = (active && ShowCheck ? "✓  " : "") + Text;
            TextRenderer.DrawText(g, text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); Update(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }
    }

    // Tarjeta de artículo: imagen, nombre y precio. Respuesta inmediata al presionar; borde verde al pasar el cursor.
    public sealed class ProductTile : Control
    {
        private readonly Product product;
        private readonly Image image;
        private readonly bool hasOptions;
        private bool hover, pressed;
        private static readonly Font NameFont = Theme.Font(10f, FontStyle.Bold);
        public event EventHandler<Product> ProductSelected;

        public ProductTile(Product item)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            product = item;
            hasOptions = ProductOptionCatalog.HasOptions(item);
            image = AppData.LoadImage(item.ImageFile);
            Width = 164;
            Height = 190;
            Margin = new Padding(0, 0, Theme.Space.Md, Theme.Space.Md);
            Cursor = Cursors.Hand;
        }

        public void SetResponsiveWidth(int width, float scale)
        {
            Width = Math.Max(110, width);
            Height = (int)(Width * 0.58) + NameFont.Height * 2 + Theme.Emphasis.Height + Theme.Space.Md * 2 + Theme.Space.Xs;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null) image.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var fill = pressed ? Theme.GreenSoft : Theme.Surface;
            using (var path = Gfx.Round(rect, Theme.Radius.Card))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(hover || pressed ? Theme.Primary : Theme.Border, hover || pressed ? 2f : 1f)) g.DrawPath(pen, path);
            }
            var pad = Theme.Space.Md;
            var imageHeight = (int)(Width * 0.58);
            if (image != null && image.Width > 0 && image.Height > 0)
            {
                var box = new Rectangle(pad, pad, Width - pad * 2, imageHeight - pad);
                var ratio = Math.Min(box.Width / (float)image.Width, box.Height / (float)image.Height);
                var w = (int)(image.Width * ratio); var h = (int)(image.Height * ratio);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, product.Name, NameFont, new Rectangle(pad, imageHeight + Theme.Space.Xs, Width - pad * 2, NameFont.Height * 2), Theme.Ink, flags);
            var price = (hasOptions ? "Desde " : "") + Theme.Money(product.Price);
            TextRenderer.DrawText(g, price, Theme.Emphasis, new Rectangle(pad, Height - pad - Theme.Emphasis.Height, Width - pad * 2, Theme.Emphasis.Height), Theme.Primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); Update(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var handler = ProductSelected;
            if (handler != null) handler(this, product);
        }
    }
}
