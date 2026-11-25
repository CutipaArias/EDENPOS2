using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class ProductsView : UserControl, IRefreshable
    {
        private const string AllCategories = "Todas las categorías";

        private readonly ScreenHeader header;
        private readonly DataGridView grid;
        private readonly TextBox search;
        private readonly ComboBox category;
        private readonly EmptyState emptyState;
        private readonly Button entryButton;
        private readonly Button exitButton;
        private readonly Button addVariantButton;
        private readonly Button editButton;
        private readonly Button deleteButton;
        private bool updatingCategories;

        private sealed class InventorySelection
        {
            public Product Product { get; set; }
            public ProductOption Option { get; set; }
            public bool IsOption { get { return Option != null; } }
            public string Key { get { return Product.Id + "|" + (Option == null ? "" : Option.Id); } }
            // Saldo en o bajo el mínimo configurado (solo si controla inventario y tiene mínimo).
            public bool IsLow
            {
                get
                {
                    return IsOption
                        ? Option.TrackInventory && Option.StockMinimo > 0 && Option.Stock <= Option.StockMinimo
                        : Product.TrackInventory && Product.StockMinimo > 0 && Product.Stock <= Product.StockMinimo;
                }
            }
        }

        public ProductsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Canvas;

            // Acciones. Cabecera: lo principal del contexto. Barra: filtros a la izquierda, acciones sobre la selección a la derecha.
            var add = Theme.Button("Nuevo artículo", ButtonVariant.Primary); add.Click += (s, e) => AddProduct();
            entryButton = Theme.Button("Registrar entrada", ButtonVariant.Secondary); entryButton.Click += (s, e) => RegisterEntry();
            exitButton = Theme.Button("Registrar salida", ButtonVariant.Secondary); exitButton.Click += (s, e) => RegisterAdjustment();
            addVariantButton = Theme.Button("Nueva variante", ButtonVariant.Secondary); addVariantButton.Width = 150; addVariantButton.Click += (s, e) => AddVariant();
            editButton = Theme.Button("Editar", ButtonVariant.Secondary); editButton.Width = 150; editButton.Click += (s, e) => EditSelected();
            deleteButton = Theme.Button("Eliminar", ButtonVariant.Destructive); deleteButton.Width = 140; deleteButton.Click += (s, e) => DeleteSelected();

            header = new ScreenHeader { Title = "Inventario" };
            header.AddAction(add);
            header.AddAction(entryButton);
            header.AddAction(exitButton);

            category = Theme.Combo();
            category.Size = new Size(230, Theme.Dim.Field);
            category.SelectedIndexChanged += (s, e) => { if (!updatingCategories) LoadProducts(); };
            search = Theme.TextBox("Buscar artículo o variante");
            search.TextChanged += (s, e) => LoadProducts();

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Md, Theme.Space.Xxl, Theme.Space.Sm) };
            var filters = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            category.Margin = new Padding(0, 2, Theme.Space.Md, 0);
            search.Width = 280; search.Margin = new Padding(0, 2, 0, 0);
            filters.Controls.Add(category);
            filters.Controls.Add(Theme.Field(search, Theme.Dim.Field));
            var rowActions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            foreach (var b in new[] { deleteButton, editButton, addVariantButton }) b.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            rowActions.Controls.Add(deleteButton);
            rowActions.Controls.Add(editButton);
            rowActions.Controls.Add(addVariantButton);
            toolbar.Controls.Add(filters);
            toolbar.Controls.Add(rowActions);

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xxl, 0, Theme.Space.Xxl, Theme.Space.Xxl), BackColor = Theme.Canvas };
            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Sm) };
            grid = new DataGridView { Dock = DockStyle.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "Categoría", Width = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Producto / variante", FillWeight = 190 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Tipo", Width = 170, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(NumberColumn("Entry", "Entrada hoy", 130));
            grid.Columns.Add(NumberColumn("Exit", "Salidas hoy", 130));
            grid.Columns.Add(NumberColumn("Balance", "Saldo", 120));
            grid.Columns.Add(NumberColumn("Price", "Precio / desde", 150));
            grid.CellPainting += PaintBalancePill;
            grid.SelectionChanged += (s, e) => UpdateActionButtons();
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };
            emptyState = Theme.EmptyState("Todavía no hay artículos", "Cree el primero con «Nuevo artículo».");
            emptyState.Visible = false;
            card.Controls.Add(grid);
            card.Controls.Add(emptyState);
            host.Controls.Add(card);

            // Orden de acople: primero el relleno, luego las franjas de abajo hacia arriba.
            Controls.Add(host);
            Controls.Add(toolbar);
            Controls.Add(header);
            RefreshView();
        }

        // Columna numérica: cifras y cabecera alineadas a la derecha.
        private static DataGridViewTextBoxColumn NumberColumn(string name, string text, int width)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = text, Width = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            return column;
        }

        // Saldo bajo el mínimo: insignia Danger (forma + color + texto, no solo color).
        private void PaintBalancePill(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Balance") return;
            var item = grid.Rows[e.RowIndex].Tag as InventorySelection;
            if (item == null || !item.IsLow) return;
            // Fondo y separador horizontal como en el resto de filas; solo se omite el texto, que lo dibuja la insignia.
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            var text = "Bajo · " + e.FormattedValue;
            var size = TextRenderer.MeasureText(text, Theme.FieldLabel, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var width = size.Width + Theme.Space.Md * 2;
            var rect = new Rectangle(e.CellBounds.Right - Theme.Space.Sm - width, e.CellBounds.Top + (e.CellBounds.Height - Theme.Dim.Pill) / 2, width, Theme.Dim.Pill);
            // El Graphics es el mismo para todas las celdas: se restaura el suavizado para no contagiar a las siguientes
            // (con AntiAlias el DataGridView pinta fondos con bordes semitransparentes = líneas oscuras entre celdas).
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedPath(rect, Theme.Dim.Pill / 2))
            using (var brush = new SolidBrush(Theme.DangerSoft))
                e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = previous;
            TextRenderer.DrawText(e.Graphics, text, Theme.FieldLabel, rect, Theme.Danger,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            e.Handled = true;
        }

        public void RefreshView()
        {
            LoadProducts(true);
        }

        private void LoadProducts(bool refreshCategories = false)
        {
            try
            {
                LoadProductsCore(refreshCategories);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Inventario (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Inventario");
            }
        }

        private void LoadProductsCore(bool refreshCategories)
        {
            if (grid == null) return;
            var selectedItem = SelectedItem();
            var selectedKey = selectedItem == null ? null : selectedItem.Key;
            var allProducts = AppData.Products;
            if (refreshCategories)
            {
                var current = category.SelectedItem == null ? AllCategories : category.SelectedItem.ToString();
                updatingCategories = true;
                category.BeginUpdate();
                try
                {
                    category.Items.Clear();
                    category.Items.Add(AllCategories);
                    foreach (var item in allProducts.Select(x => x.Category).Distinct().OrderBy(x => x)) category.Items.Add(item);
                    var index = category.Items.IndexOf(current);
                    category.SelectedIndex = index >= 0 ? index : 0;
                }
                finally
                {
                    category.EndUpdate();
                    updatingCategories = false;
                }
            }
            if (category.SelectedItem == null) return;
            var query = allProducts.AsEnumerable();
            var selectedCategory = category.SelectedItem.ToString();
            if (selectedCategory != AllCategories) query = query.Where(x => x.Category == selectedCategory);
            var term = (search.Text ?? "").Trim();
            if (term.Length > 0)
                query = query.Where(product =>
                    product.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    ProductOptionCatalog.GetOptions(product).Any(option =>
                        option.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0));
            var products = query.OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();

            var activity = AppData.GetInventoryActivity(DateTime.Today);

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var product in products)
            {
                var options = ProductOptionCatalog.GetOptions(product).ToList();
                var entryCount = InventoryActivity.Count(activity.Entries, product.Id);
                var exitCount = InventoryActivity.Count(activity.Exits, product.Id);
                var balance = product.TrackInventory ? product.Stock.ToString() : "—";
                // RF-14: Producto o Implemento; en un grupo se agrega cuántas variantes tiene.
                var type = product.Tipo + (options.Count > 0 ? " · " + options.Count + (options.Count == 1 ? " variante" : " variantes") : "");
                var row = grid.Rows.Add(product.Category, product.Name, type, entryCount, exitCount, balance, Theme.Money(product.Price));
                var productSelection = new InventorySelection { Product = product };
                var productRow = grid.Rows[row];
                productRow.Tag = productSelection;
                StyleCounts(productRow, entryCount, exitCount, product.TrackInventory);
                if (options.Count > 0)
                {
                    // Grupo: fila hundida con nombre en semibold; sus variantes cuelgan debajo, sangradas.
                    productRow.DefaultCellStyle.BackColor = Theme.SurfaceSunken;
                    productRow.Cells["Name"].Style.Font = Theme.Subhead;
                    productRow.Cells["Type"].Style.ForeColor = Theme.Primary;
                    productRow.Cells["Price"].Style.Font = Theme.Subhead;
                }
                if (!product.Active) productRow.DefaultCellStyle.ForeColor = Theme.Muted;
                if (productSelection.Key == selectedKey) productRow.Selected = true;

                var visibleOptions = term.Length > 0 &&
                                     product.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0
                    ? options.Where(option => option.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    : options;
                foreach (var option in visibleOptions)
                {
                    var optionEntry = InventoryActivity.Count(activity.Entries, option.Id);
                    var optionExit = activity.OptionExits(product.Id, option.Id, product.Name + " · " + option.Name);
                    var optionBalance = option.TrackInventory ? option.Stock.ToString() : "—";
                    var optionRow = grid.Rows.Add("", "↳  " + option.Name, "Variante", optionEntry, optionExit, optionBalance, Theme.Money(option.Price));
                    var optionSelection = new InventorySelection { Product = product, Option = option };
                    var variantRow = grid.Rows[optionRow];
                    variantRow.Tag = optionSelection;
                    StyleCounts(variantRow, optionEntry, optionExit, option.TrackInventory);
                    variantRow.Cells["Name"].Style.Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Sm, 0);
                    variantRow.Cells["Type"].Style.ForeColor = Theme.Muted;
                    if (!product.Active) variantRow.DefaultCellStyle.ForeColor = Theme.Muted;
                    if (optionSelection.Key == selectedKey) variantRow.Selected = true;
                }
            }
            if (grid.Rows.Count > 0 && grid.SelectedRows.Count == 0) grid.Rows[0].Selected = true;
            grid.ResumeLayout();
            UpdateSummary(allProducts, products.Count, term.Length > 0 || selectedCategory != AllCategories);
            UpdateActionButtons();
        }

        // Ceros y "—" en gris: lo que no aporta no compite con las cifras que sí.
        private static void StyleCounts(DataGridViewRow row, int entryCount, int exitCount, bool tracked)
        {
            if (entryCount == 0) row.Cells["Entry"].Style.ForeColor = Theme.TextTertiary;
            if (exitCount == 0) row.Cells["Exit"].Style.ForeColor = Theme.TextTertiary;
            if (!tracked) row.Cells["Balance"].Style.ForeColor = Theme.TextTertiary;
        }

        private void UpdateSummary(List<Product> all, int shown, bool filtered)
        {
            var low = all.Count(p => (p.TrackInventory && p.StockMinimo > 0 && p.Stock <= p.StockMinimo) ||
                                     ProductOptionCatalog.GetOptions(p).Any(o => o.TrackInventory && o.StockMinimo > 0 && o.Stock <= o.StockMinimo));
            var text = all.Count + (all.Count == 1 ? " artículo · " : " artículos · ") + (low == 0 ? "ninguno bajo su mínimo" : low + " bajo su mínimo");
            if (filtered) text += " · mostrando " + shown;
            header.Subtitle = text;

            var empty = grid.Rows.Count == 0;
            grid.Visible = !empty;
            emptyState.Visible = empty;
            if (empty)
            {
                emptyState.Title = filtered ? "Ningún artículo coincide con el filtro" : "Todavía no hay artículos";
                emptyState.Hint = filtered ? "Pruebe con otra búsqueda o elija «" + AllCategories + "»." : "Cree el primero con «Nuevo artículo».";
            }
        }

        private InventorySelection SelectedItem()
        {
            return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as InventorySelection;
        }

        private Product SelectedProduct()
        {
            var item = SelectedItem();
            return item == null ? null : item.Product;
        }

        private void UpdateActionButtons()
        {
            var item = SelectedItem();
            var hasSelection = item != null;
            entryButton.Enabled = hasSelection;
            // RF-15: la salida (pérdida, consumo interno, ajuste) solo aplica a artículos que controlan inventario.
            exitButton.Enabled = hasSelection && (item.IsOption ? item.Option.TrackInventory : item.Product.TrackInventory);
            addVariantButton.Enabled = hasSelection;
            editButton.Enabled = hasSelection;
            deleteButton.Enabled = hasSelection;
            editButton.Text = hasSelection && item.IsOption ? "Editar variante" : "Editar";
            deleteButton.Text = hasSelection && item.IsOption ? "Quitar variante" : "Eliminar";
        }

        private void AddProduct()
        {
            using (var dialog = new ProductEditorDialog(null, AppData.Products.Select(x => x.Category).Distinct()))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                var products = AppData.Products;
                if (products.Any(x => x.Name == dialog.Product.Name && x.Category == dialog.Product.Category))
                {
                    Theme.Message(FindForm(), Tone.Warning, "Ya existe un artículo con el mismo nombre en esa categoría.", "Validación de artículos");
                    return;
                }
                products.Add(dialog.Product);
                if (!TrySaveProducts(products)) return;
                RefreshView();
                SelectProduct(dialog.Product.Id);
                if (dialog.Product.TrackInventory)
                {
                    RegisterEntry();
                    RefreshView();
                }
            }
        }

        private void AddVariant()
        {
            var selected = SelectedItem();
            if (selected == null) return;
            using (var dialog = new ProductOptionEditorDialog(selected.Product.Name, null))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                var products = AppData.Products;
                var product = products.FirstOrDefault(x => x.Id == selected.Product.Id);
                if (product == null) return;
                if (product.Options == null) product.Options = new List<ProductOption>();
                if (product.Options.Any(x => string.Equals(x.Name, dialog.Option.Name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    Theme.Message(FindForm(), Tone.Warning, "Ya existe una variante con ese nombre.", "Variantes");
                    return;
                }
                product.Options.Add(dialog.Option);
                product.Price = product.Options.Min(x => x.Price);
                product.TrackInventory = false;
                if (!TrySaveProducts(products)) return;
                RefreshView();
                SelectItem(product.Id, dialog.Option.Id);
            }
        }

        private void EditSelected()
        {
            var selection = SelectedItem();
            if (selection == null) return;
            if (selection.IsOption)
            {
                using (var dialog = new ProductOptionEditorDialog(selection.Product.Name, selection.Option))
                {
                    if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                    var products = AppData.Products;
                    var product = products.FirstOrDefault(x => x.Id == selection.Product.Id);
                    if (product == null || product.Options == null) return;
                    if (product.Options.Any(x => x.Id != selection.Option.Id &&
                                                  string.Equals(x.Name, dialog.Option.Name, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        Theme.Message(FindForm(), Tone.Warning, "Ya existe otra variante con ese nombre.", "Variantes");
                        return;
                    }
                    var storedOption = product.Options.FirstOrDefault(x => x.Id == selection.Option.Id);
                    if (storedOption == null) return;
                    storedOption.Name = dialog.Option.Name;
                    storedOption.Price = dialog.Option.Price;
                    storedOption.StockMinimo = dialog.Option.StockMinimo;
                    product.Price = product.Options.Min(x => x.Price);
                    if (!TrySaveProducts(products)) return;
                    RefreshView();
                    SelectItem(product.Id, storedOption.Id);
                }
                return;
            }

            var selected = selection.Product;
            using (var dialog = new ProductEditorDialog(selected, AppData.Products.Select(x => x.Category).Distinct()))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                var products = AppData.Products;
                if (products.Any(x => x.Id != selected.Id && x.Name == dialog.Product.Name && x.Category == dialog.Product.Category))
                {
                    Theme.Message(FindForm(), Tone.Warning, "Ya existe otro artículo con el mismo nombre en esa categoría.", "Validación de artículos");
                    return;
                }
                var stored = products.FirstOrDefault(x => x.Id == selected.Id);
                if (stored != null)
                {
                    stored.Name = dialog.Product.Name;
                    stored.Category = dialog.Product.Category;
                    stored.Price = stored.Options != null && stored.Options.Count > 0 ? stored.Options.Min(x => x.Price) : dialog.Product.Price;
                    stored.ColorHex = dialog.Product.ColorHex;
                    stored.ImageFile = dialog.Product.ImageFile;
                    stored.Active = dialog.Product.Active;
                    stored.TrackInventory = dialog.Product.TrackInventory;
                    stored.StockMinimo = dialog.Product.StockMinimo;
                    stored.CargoPerdida = dialog.Product.CargoPerdida;
                    stored.Tipo = dialog.Product.Tipo;
                }
                if (!TrySaveProducts(products)) return;
                RefreshView();
            }
        }

        private void ToggleSelected()
        {
            var selected = SelectedProduct();
            if (selected == null) return;
            var products = AppData.Products;
            var stored = products.FirstOrDefault(x => x.Id == selected.Id);
            if (stored == null) return;
            stored.Active = !stored.Active;
            if (!TrySaveProducts(products)) return;
            RefreshView();
        }

        
        // Guarda los productos y, si falla por validación o por la base de datos, avisa y refresca la vista.
        private bool TrySaveProducts(List<Product> products)
        {
            try
            {
                AppData.SaveProducts(products);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo guardar");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Guardar productos");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo guardar");
            }
            RefreshAfterFailure();
            return false;
        }

        // Refresca tras un fallo sin que un segundo fallo de MySQL escape del catch del llamador.
        private void RefreshAfterFailure()
        {
            try
            {
                RefreshView();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Inventario: refrescar tras un error (base de datos)");
            }
        }

        private void SelectProduct(string id)
        {
            SelectItem(id, null);
        }

        private void SelectItem(string productId, string optionId)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var item = row.Tag as InventorySelection;
                if (item == null || item.Product.Id != productId) continue;
                if ((item.Option == null ? null : item.Option.Id) != optionId) continue;
                row.Selected = true;
                grid.CurrentCell = row.Cells["Name"];
                return;
            }
        }

        private void RegisterEntry()
        {
            var item = SelectedItem();
            var selected = item == null ? null : item.Product;
            if (selected == null) { Theme.Message(FindForm(), Tone.Info, "Seleccione primero un producto.", "Inventario"); return; }
            var displayName = item.IsOption ? selected.Name + " - " + item.Option.Name : selected.Name;
            var currentStock = item.IsOption ? item.Option.Stock : selected.Stock;
            using (var dialog = new InventoryEntryDialog(displayName, currentStock))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    if (item.IsOption)
                        AppData.AddInventoryEntry(selected.Id, item.Option.Id, dialog.Quantity, dialog.Note);
                    else
                        AppData.AddInventoryEntry(selected.Id, dialog.Quantity, dialog.Note);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo registrar la entrada");
                    RefreshAfterFailure();
                    return;
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Registrar entrada de inventario");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo registrar la entrada");
                    RefreshAfterFailure();
                    return;
                }
                RefreshView();
                SelectItem(selected.Id, item.IsOption ? item.Option.Id : null);
            }
        }

        // RF-15: pérdida, consumo interno o ajuste por conteo físico, siempre con motivo.
        private void RegisterAdjustment()
        {
            var item = SelectedItem();
            var selected = item == null ? null : item.Product;
            if (selected == null) { Theme.Message(FindForm(), Tone.Info, "Seleccione primero un producto.", "Inventario"); return; }
            var displayName = item.IsOption ? selected.Name + " - " + item.Option.Name : selected.Name;
            var currentStock = item.IsOption ? item.Option.Stock : selected.Stock;
            using (var dialog = new InventoryAdjustmentDialog(displayName, currentStock))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    AppData.AddInventoryAdjustment(selected.Id, item.IsOption ? item.Option.Id : null, dialog.MovementType, dialog.Quantity, dialog.Reason);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo registrar la salida");
                    RefreshAfterFailure();
                    return;
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Registrar salida de inventario");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se pudo registrar la salida");
                    RefreshAfterFailure();
                    return;
                }
                RefreshView();
                SelectItem(selected.Id, item.IsOption ? item.Option.Id : null);
            }
        }

        private void DeleteSelected()
        {
            var item = SelectedItem();
            if (item == null) return;
            if (item.IsOption)
            {
                if (MessageBox.Show("¿Quitar la variante “" + item.Option.Name + "” de " + item.Product.Name + "?", "Confirmar variante", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var products = AppData.Products;
                var product = products.FirstOrDefault(x => x.Id == item.Product.Id);
                if (product == null || product.Options == null) return;
                product.Options.RemoveAll(x => x.Id == item.Option.Id);
                if (product.Options.Count > 0) product.Price = product.Options.Min(x => x.Price);
                if (!TrySaveProducts(products)) return;
                RefreshView();
                SelectProduct(product.Id);
                return;
            }
            var selected = item.Product;
            if (MessageBox.Show("¿Desea eliminar “" + selected.Name + "”?", "Confirmar eliminación", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { AppData.DeleteProduct(selected.Id); }
            catch (InvalidOperationException ex) { Theme.Message(FindForm(), Tone.Info, ex.Message, "No se puede eliminar"); }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Eliminar producto");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "No se puede eliminar");
            }
        }
    }

    // Piezas comunes de los diálogos de inventario (mismo ritmo vertical y misma fila de acciones).
    internal static class InventoryDialogKit
    {
        public const int Margin = 28;   // margen interior del diálogo

        public static void Setup(Form form, int width, int height)
        {
            form.StartPosition = FormStartPosition.CenterParent;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.ClientSize = new Size(width, height);
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.BackColor = Theme.Surface;
            form.Font = Theme.Body;
        }

        // Etiqueta de campo + campo; devuelve la Y siguiente.
        public static int AddField(Control parent, string label, Control inner, int x, int y, int width, out EdenField field)
        {
            var l = Theme.LabelField(label); l.Location = new Point(x, y);
            parent.Controls.Add(l);
            inner.SetBounds(x, y + 22, width, Theme.Dim.Field);
            field = Theme.Field(inner, Theme.Dim.Field);
            parent.Controls.Add(field);
            return y + 22 + Theme.Dim.Field;
        }

        public static Label AddNote(Control parent, string text, int x, int y, int width, int height)
        {
            var note = Theme.Label(text, Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
            note.AutoSize = false; note.SetBounds(x, y, width, height);
            parent.Controls.Add(note);
            return note;
        }

        // Fila inferior: separador fino, Cancelar (secundario) y la acción principal a la derecha.
        public static void AddActions(Form form, Button primary, Button cancel, int left, int width, int y)
        {
            var line = Theme.Separator(); line.SetBounds(left, y - Theme.Space.Lg, width, 1);
            form.Controls.Add(line);
            primary.Size = new Size(Math.Max(150, primary.GetPreferredSize(Size.Empty).Width), Theme.Dim.Control);
            cancel.Size = new Size(Math.Max(110, cancel.GetPreferredSize(Size.Empty).Width), Theme.Dim.Control);
            primary.Location = new Point(left + width - primary.Width, y);
            cancel.Location = new Point(primary.Left - Theme.Space.Sm - cancel.Width, y);
            cancel.DialogResult = DialogResult.Cancel;
            form.Controls.Add(primary);
            form.Controls.Add(cancel);
            form.AcceptButton = primary as IButtonControl;
            form.CancelButton = cancel;
        }
    }

    public sealed class InventoryEntryDialog : Form
    {
        private readonly NumericUpDown quantity;
        private readonly TextBox note;
        public int Quantity { get { return (int)quantity.Value; } }
        public string Note { get { return note.Text.Trim(); } }

        public InventoryEntryDialog(Product product)
            : this(product.Name, product.Stock)
        {
        }

        public InventoryEntryDialog(string displayName, int currentStock)
        {
            Text = "Registrar entrada";
            WindowChrome.Integrate(this);
            const int m = InventoryDialogKit.Margin;
            const int w = 424;
            InventoryDialogKit.Setup(this, w + m * 2, 372);

            var title = new Label { Text = displayName, Font = Theme.Title, ForeColor = Theme.Ink, AutoEllipsis = true, AutoSize = false };
            title.SetBounds(m, m, w, 30);
            var balance = Theme.Label("", Theme.Body.Size, FontStyle.Regular, Theme.Muted);
            balance.AutoSize = false; balance.SetBounds(m, m + 34, w, 24);
            Controls.Add(title); Controls.Add(balance);

            quantity = new NumericUpDown { Font = Theme.Emphasis, Minimum = 1, Maximum = 1000000, Value = 1, ThousandsSeparator = true };
            EdenField quantityField;
            var y = InventoryDialogKit.AddField(this, "Cantidad que ingresa", quantity, m, 92, w, out quantityField);
            note = new TextBox { Font = Theme.Body, MaxLength = 120 };
            EdenField noteField;
            y = InventoryDialogKit.AddField(this, "Comentario (opcional)", note, m, y + Theme.Space.Lg, w, out noteField);
            noteField.Placeholder = "Por ejemplo: compra a proveedor";

            // Saldo actual y cómo quedará: respuesta inmediata mientras se escribe la cantidad.
            Action refreshBalance = () =>
            {
                var after = (long)currentStock + (long)quantity.Value;
                balance.Text = "Saldo actual: " + currentStock + "   →   quedará en " + after;
            };
            quantity.ValueChanged += (s, e) => refreshBalance();
            refreshBalance();

            var save = Theme.Button("Guardar entrada", ButtonVariant.Primary); save.DialogResult = DialogResult.OK;
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            InventoryDialogKit.AddActions(this, save, cancel, m, w, ClientSize.Height - m - Theme.Dim.Control + Theme.Space.Sm);
            Theme.FitDialogToWorkingArea(this);
        }
    }

    // RF-15: salida de inventario que no es venta. "Ajuste por conteo" pide el stock contado; los demás, la cantidad que sale.
    public sealed class InventoryAdjustmentDialog : Form
    {
        private static readonly string[] Types = { InventoryMovementTypes.Perdida, InventoryMovementTypes.ConsumoInterno, InventoryMovementTypes.Ajuste };
        private static readonly string[] TypeLabels = { "Pérdida (roto, vencido, extraviado)", "Consumo interno (uso del local)", "Ajuste por conteo físico" };

        private readonly ComboBox type;
        private readonly NumericUpDown quantity;
        private readonly TextBox reason;
        public string MovementType { get { return Types[Math.Max(0, type.SelectedIndex)]; } }
        public int Quantity { get { return (int)quantity.Value; } }
        public string Reason { get { return reason.Text.Trim(); } }

        public InventoryAdjustmentDialog(string displayName, int currentStock)
        {
            Text = "Registrar salida";
            WindowChrome.Integrate(this);
            const int m = InventoryDialogKit.Margin;
            const int w = 424;
            InventoryDialogKit.Setup(this, w + m * 2, 452);

            var title = new Label { Text = displayName, Font = Theme.Title, ForeColor = Theme.Ink, AutoEllipsis = true, AutoSize = false };
            title.SetBounds(m, m, w, 30);
            var balance = Theme.Label("", Theme.Body.Size, FontStyle.Regular, Theme.Muted);
            balance.AutoSize = false; balance.SetBounds(m, m + 34, w, 24);
            Controls.Add(title); Controls.Add(balance);

            type = Theme.Combo();
            type.Items.AddRange(TypeLabels);
            type.SelectedIndex = 0;
            EdenField typeField;
            var y = InventoryDialogKit.AddField(this, "Tipo de salida", type, m, 92, w, out typeField);
            quantity = new NumericUpDown { Font = Theme.Emphasis, Minimum = 1, Maximum = Math.Max(1, currentStock), Value = 1, ThousandsSeparator = true };
            EdenField quantityField;
            var quantityTop = y + Theme.Space.Lg;
            y = InventoryDialogKit.AddField(this, "Cantidad que sale", quantity, m, quantityTop, w, out quantityField);
            var quantityLabel = Controls.OfType<Label>().Last(l => l.Top == quantityTop);
            reason = new TextBox { Font = Theme.Body, MaxLength = 150 };
            EdenField reasonField;
            InventoryDialogKit.AddField(this, "Motivo (obligatorio)", reason, m, y + Theme.Space.Lg, w, out reasonField);
            reasonField.Placeholder = "Por ejemplo: botella rota al reponer";

            // Respuesta inmediata: saldo actual y cómo quedará.
            Action refresh = () =>
            {
                var isCount = MovementType == InventoryMovementTypes.Ajuste;
                quantityLabel.Text = isCount ? "Stock contado" : "Cantidad que sale";
                var after = isCount ? (long)quantity.Value : (long)currentStock - (long)quantity.Value;
                balance.Text = "Saldo actual: " + currentStock + "   →   quedará en " + after;
            };
            type.SelectedIndexChanged += (s, e) =>
            {
                var isCount = MovementType == InventoryMovementTypes.Ajuste;
                quantity.Minimum = isCount ? 0 : 1;
                quantity.Maximum = isCount ? 1000000 : Math.Max(1, currentStock);
                quantity.Value = isCount ? Math.Max(0, currentStock) : 1;
                refresh();
            };
            quantity.ValueChanged += (s, e) => refresh();
            refresh();

            var save = Theme.Button("Guardar salida", ButtonVariant.Primary);
            save.Click += (s, e) =>
            {
                if (!Validation.IsValidRequiredText(reason.Text, 3, 150))
                {
                    Theme.Message(this, Tone.Warning, "Ingrese un motivo de 3 a 150 caracteres.", "Registrar salida");
                    reason.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            InventoryDialogKit.AddActions(this, save, cancel, m, w, ClientSize.Height - m - Theme.Dim.Control + Theme.Space.Sm);
            Theme.FitDialogToWorkingArea(this);
        }
    }

    public sealed class ProductOptionEditorDialog : Form
    {
        private readonly TextBox name;
        private readonly NumericUpDown price;
        private readonly NumericUpDown stockMinimo;
        private readonly bool existingTrackInventory;
        private readonly string optionId;
        public ProductOption Option { get; private set; }

        public ProductOptionEditorDialog(string productName, ProductOption existing)
        {
            optionId = existing == null || string.IsNullOrWhiteSpace(existing.Id)
                ? Guid.NewGuid().ToString("N")
                : existing.Id;
            existingTrackInventory = existing != null && existing.TrackInventory;
            Text = existing == null ? "Nueva variante" : "Editar variante";
            WindowChrome.Integrate(this);
            const int m = InventoryDialogKit.Margin;
            const int w = 464;
            InventoryDialogKit.Setup(this, w + m * 2, 420);

            var title = new Label { Text = productName, Font = Theme.Title, ForeColor = Theme.Ink, AutoEllipsis = true, AutoSize = false };
            title.SetBounds(m, m, w, 30);
            var subtitle = Theme.Label(existing == null ? "Nueva variante" : "Editar variante", Theme.Body.Size, FontStyle.Regular, Theme.Muted);
            subtitle.AutoSize = false; subtitle.SetBounds(m, m + 34, w, 24);
            Controls.Add(title); Controls.Add(subtitle);

            name = new TextBox { Font = Theme.Body, MaxLength = 80, Text = existing == null ? "" : existing.Name };
            EdenField nameField;
            var y = InventoryDialogKit.AddField(this, "Nombre de la variante", name, m, 92, w, out nameField);

            const int half = (w - Theme.Space.Lg) / 2;
            price = new NumericUpDown
            {
                Font = Theme.Body, DecimalPlaces = 2, Minimum = 0, Maximum = 1000000, ThousandsSeparator = true,
                Value = existing == null ? 0 : existing.Price
            };
            EdenField priceField;
            var next = InventoryDialogKit.AddField(this, "Precio (S/)", price, m, y + Theme.Space.Lg, half, out priceField);
            stockMinimo = new NumericUpDown
            {
                Font = Theme.Body, Minimum = 0, Maximum = 1000000,
                Value = existing == null ? 0 : Math.Max(0, Math.Min(1000000, existing.StockMinimo))
            };
            EdenField stockField;
            InventoryDialogKit.AddField(this, "Stock mínimo", stockMinimo, m + half + Theme.Space.Lg, y + Theme.Space.Lg, half, out stockField);
            stockField.Enabled = existingTrackInventory;
            InventoryDialogKit.AddNote(this, existingTrackInventory
                ? "El stock mínimo avisa cuando el saldo de esta variante llega a ese valor. Déjelo en 0 para no recibir avisos."
                : "El stock mínimo se activa cuando esta variante controla inventario.", m, next + Theme.Space.Sm, w, 40);

            var save = Theme.Button("Guardar", ButtonVariant.Primary); save.Click += Save_Click;
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            InventoryDialogKit.AddActions(this, save, cancel, m, w, ClientSize.Height - m - Theme.Dim.Control + Theme.Space.Sm);
            Shown += (s, e) => { name.SelectAll(); name.Focus(); };
            Theme.FitDialogToWorkingArea(this);
        }

        private void Save_Click(object sender, EventArgs e)
        {
            if (!Validation.IsValidRequiredText(name.Text, 2, 80))
            {
                Theme.Message(this, Tone.Warning, "El nombre debe tener entre 2 y 80 caracteres.", "Variante");
                name.Focus();
                return;
            }
            Option = new ProductOption
            {
                Id = optionId,
                Name = name.Text.Trim().ToUpperInvariant(),
                Price = price.Value,
                StockMinimo = existingTrackInventory ? (int)stockMinimo.Value : 0
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // Muestra de color redondeada con borde (la que abre el selector de color al hacer clic).
    internal sealed class ColorSwatch : Control
    {
        private Color swatch;

        public ColorSwatch(Color color)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            swatch = color;
            Cursor = Cursors.Hand;
            Height = Theme.Dim.Field;
        }

        public Color Swatch { get { return swatch; } set { swatch = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var path = Gfx.Round(rect, Theme.Radius.Control))
            {
                using (var brush = new SolidBrush(swatch)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.BorderStrong)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    public sealed class ProductEditorDialog : Form
    {
        private readonly TextBox name;
        private readonly ComboBox category;
        private readonly NumericUpDown price;
        private readonly NumericUpDown cargoPerdida;
        private readonly ComboBox tipo;
        private readonly CheckBox trackInventoryCheck;
        private readonly NumericUpDown stockMinimo;
        private readonly EdenField stockMinimoField;
        private readonly PictureBox preview;
        private readonly ColorSwatch colorSwatch;
        private string imageFile;
        private string selectedSource;
        private Color selectedColor;
        private readonly bool existingActive;
        private readonly bool existingTrackInventory;
        private readonly int existingStock;
        private readonly int existingInitialStock;

        public Product Product { get; private set; }

        private bool IsImplementSelected { get { return (tipo.SelectedItem as string) == ProductTypes.Implemento; } }

        public ProductEditorDialog(Product existing, IEnumerable<string> categories)
        {
            Text = existing == null ? "Nuevo artículo" : "Editar artículo";
            WindowChrome.Integrate(this);
            const int sideWidth = 238, m = InventoryDialogKit.Margin, w = 408;
            InventoryDialogKit.Setup(this, sideWidth + m + w + m, 638);
            existingActive = existing == null || existing.Active;
            existingTrackInventory = existing != null && existing.TrackInventory;
            existingStock = existing == null ? 0 : existing.Stock;
            existingInitialStock = existing == null ? 0 : existing.InitialStock;
            imageFile = existing == null ? null : existing.ImageFile;
            selectedColor = Theme.ParseColor(existing == null ? "#25AE4A" : existing.ColorHex, Theme.Green);

            // Columna izquierda: imagen y color del mosaico, sobre el lienzo.
            var left = new Panel { Location = new Point(0, 0), Size = new Size(sideWidth, ClientSize.Height), BackColor = Theme.Canvas };
            var previewCard = new CardPanel { Location = new Point(24, 28), Size = new Size(190, 190), Padding = new Padding(Theme.Space.Md) };
            preview = new PictureBox { Location = new Point(Theme.Space.Md, Theme.Space.Md), Size = new Size(190 - Theme.Space.Md * 2, 190 - Theme.Space.Md * 2), BackColor = Theme.Surface, SizeMode = PictureBoxSizeMode.Zoom, Image = AppData.LoadImage(imageFile) };
            previewCard.Controls.Add(preview);
            var choose = Theme.Button("Elegir imagen", ButtonVariant.Secondary); choose.SetBounds(24, 230, 190, Theme.Dim.Control); choose.Click += ChooseImage;
            var colorLabel = Theme.LabelField("Color del mosaico"); colorLabel.Location = new Point(24, 296);
            colorSwatch = new ColorSwatch(selectedColor) { Location = new Point(24, 318), Size = new Size(190, Theme.Dim.Field) };
            colorSwatch.Click += ChooseColor;
            var hint = Theme.Label("La imagen se copia a la carpeta local de datos del programa.", Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
            hint.AutoSize = false; hint.SetBounds(24, 368, 190, 48);
            left.Controls.Add(previewCard); left.Controls.Add(choose); left.Controls.Add(colorLabel); left.Controls.Add(colorSwatch); left.Controls.Add(hint);
            Controls.Add(left);

            var x = sideWidth + m;
            name = new TextBox { Font = Theme.Body, Text = existing == null ? "" : existing.Name };
            EdenField nameField;
            var y = InventoryDialogKit.AddField(this, "Nombre del artículo", name, x, 28, w, out nameField);
            category = new ComboBox { Font = Theme.Body, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat };
            foreach (var item in categories.OrderBy(c => c)) category.Items.Add(item);
            category.Text = existing == null ? (category.Items.Count > 0 ? category.Items[0].ToString() : "GENERAL") : existing.Category;
            EdenField categoryField;
            y = InventoryDialogKit.AddField(this, "Categoría", category, x, y + Theme.Space.Lg, w, out categoryField);
            price = new NumericUpDown
            {
                Font = Theme.Body, DecimalPlaces = 2, Maximum = 1000000, Minimum = 0, ThousandsSeparator = true,
                Value = existing == null ? 0 : existing.Price
            };
            EdenField priceField;
            var priceTop = y + Theme.Space.Lg;
            y = InventoryDialogKit.AddField(this, "Precio (S/)", price, x, priceTop, 190, out priceField);
            // RF-14: Producto (se vende o se consume) o Implemento (se presta y se devuelve). Solo los implementos se revisan al finalizar la visita.
            tipo = Theme.Combo();
            tipo.Items.AddRange(new object[] { ProductTypes.Producto, ProductTypes.Implemento });
            tipo.SelectedItem = existing == null ? ProductTypes.Infer(category.Text, 0m) : ProductTypes.Normalize(existing.Tipo, existing.Category, existing.CargoPerdida);
            EdenField tipoField;
            InventoryDialogKit.AddField(this, "Tipo de artículo", tipo, x + 190 + Theme.Space.Lg, priceTop, w - 190 - Theme.Space.Lg, out tipoField);
            // RN-07 / RF-22: lo que se cobra por unidad si el cliente no devuelve este implemento (0 = sin cargo, pero se registra igual).
            cargoPerdida = new NumericUpDown
            {
                Font = Theme.Body, DecimalPlaces = 2, Maximum = AppData.CargoPerdidaMaximo, Minimum = 0, ThousandsSeparator = true,
                Value = existing == null ? 0 : Math.Max(0, Math.Min(AppData.CargoPerdidaMaximo, existing.CargoPerdida))
            };
            EdenField cargoField;
            var cargoTop = y + Theme.Space.Lg;
            y = InventoryDialogKit.AddField(this, "Cargo por pérdida (S/)", cargoPerdida, x, cargoTop, 190, out cargoField);
            var cargoNote = InventoryDialogKit.AddNote(this, "", x + 190 + Theme.Space.Lg, cargoTop + 22, w - 190 - Theme.Space.Lg, Theme.Dim.Field);
            Action syncCargo = () =>
            {
                var isImplement = IsImplementSelected;
                cargoField.Enabled = isImplement;
                if (!isImplement) cargoPerdida.Value = 0;
                cargoNote.Text = isImplement ? "Se cobra por cada unidad que el cliente no devuelva." : "Solo los implementos tienen cargo por pérdida.";
            };
            tipo.SelectedIndexChanged += (s, e) => syncCargo();
            syncCargo();

            // Grupo de inventario: casilla + mínimo juntos, con su explicación.
            var group = new CardPanel { BackColor = Theme.Canvas, Location = new Point(x, y + Theme.Space.Xl), Size = new Size(w, 168), Padding = new Padding(1) };
            trackInventoryCheck = new CheckBox
            {
                Text = "Controla inventario", Font = Theme.Subhead, ForeColor = Theme.Ink, BackColor = Theme.Canvas,
                Location = new Point(Theme.Space.Lg, Theme.Space.Lg), Size = new Size(w - Theme.Space.Lg * 2, 26), Checked = existingTrackInventory
            };
            var trackNote = Theme.Label("Lleva el saldo y lo descuenta en cada venta. Al guardar podrá registrar la primera entrada.", Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
            trackNote.AutoSize = false; trackNote.BackColor = Theme.Canvas; trackNote.SetBounds(Theme.Space.Lg + 24, Theme.Space.Lg + 28, w - Theme.Space.Lg * 2 - 24, 34);
            var minLabel = Theme.LabelField("Stock mínimo"); minLabel.BackColor = Theme.Canvas; minLabel.Location = new Point(Theme.Space.Lg, 82);
            stockMinimo = new NumericUpDown
            {
                Font = Theme.Body, Minimum = 0, Maximum = 1000000,
                Value = existing == null ? 0 : Math.Max(0, Math.Min(1000000, existing.StockMinimo))
            };
            stockMinimo.SetBounds(Theme.Space.Lg, 104, 120, Theme.Dim.Field);
            stockMinimoField = Theme.Field(stockMinimo, Theme.Dim.Field);
            stockMinimoField.Enabled = existingTrackInventory;
            var minNote = Theme.Label("Avisa cuando el saldo llegue a este valor.\nDéjelo en 0 para no recibir avisos.", Theme.Caption.Size, FontStyle.Regular, Theme.Muted);
            minNote.AutoSize = false; minNote.BackColor = Theme.Canvas; minNote.SetBounds(Theme.Space.Lg + 120 + Theme.Space.Md, 104, w - Theme.Space.Lg * 2 - 120 - Theme.Space.Md, 40);
            group.Controls.Add(trackInventoryCheck); group.Controls.Add(trackNote); group.Controls.Add(minLabel); group.Controls.Add(stockMinimoField); group.Controls.Add(minNote);
            trackInventoryCheck.CheckedChanged += (s, e) => stockMinimoField.Enabled = trackInventoryCheck.Checked;
            Controls.Add(group);

            var save = Theme.Button("Guardar", ButtonVariant.Primary); save.Click += Save_Click;
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            InventoryDialogKit.AddActions(this, save, cancel, x, w, ClientSize.Height - m - Theme.Dim.Control + Theme.Space.Sm);
            Theme.FitDialogToWorkingArea(this);
        }

        private void ChooseImage(object sender, EventArgs e)
        {
            using (var open = new OpenFileDialog { Filter = "Imágenes (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" })
            {
                if (open.ShowDialog(this) != DialogResult.OK) return;
                selectedSource = open.FileName;
                if (preview.Image != null) preview.Image.Dispose();
                preview.Image = AppData.LoadImage(open.FileName);
            }
        }

        private void ChooseColor(object sender, EventArgs e)
        {
            using (var dialog = new ColorDialog { Color = selectedColor, FullOpen = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                selectedColor = dialog.Color;
                colorSwatch.Swatch = selectedColor;
            }
        }

        private void Save_Click(object sender, EventArgs e)
        {
            if (!Validation.IsValidRequiredText(name.Text, 2, 60))
            {
                Theme.Message(this, Tone.Warning, "El nombre debe tener entre 2 y 60 caracteres.", "Validación del artículo");
                name.Focus();
                return;
            }
            if (!Validation.IsValidRequiredText(category.Text, 2, 40))
            {
                Theme.Message(this, Tone.Warning, "La categoría debe tener entre 2 y 40 caracteres.", "Validación del artículo");
                category.Focus();
                return;
            }
            if (!string.IsNullOrWhiteSpace(selectedSource))
            {
                var folder = Path.Combine(AppData.DataDirectory, "Images");
                Directory.CreateDirectory(folder);
                var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(selectedSource).ToLowerInvariant());
                File.Copy(selectedSource, destination, true);
                imageFile = "data:" + Path.GetFileName(destination);
            }
            Product = new Product
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Text.Trim().ToUpperInvariant(),
                Category = category.Text.Trim().ToUpperInvariant(),
                Price = price.Value,
                ImageFile = imageFile,
                ColorHex = ColorTranslator.ToHtml(selectedColor),
                Active = existingActive,
                TrackInventory = trackInventoryCheck.Checked,
                StockMinimo = trackInventoryCheck.Checked ? (int)stockMinimo.Value : 0,
                CargoPerdida = IsImplementSelected ? Math.Round(cargoPerdida.Value, 2) : 0m,
                Tipo = IsImplementSelected ? ProductTypes.Implemento : ProductTypes.Producto,
                Stock = existingStock,
                InitialStock = existingInitialStock
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
