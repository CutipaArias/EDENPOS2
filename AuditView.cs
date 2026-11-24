using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MySqlConnector;

namespace EdenPOS
{
    // Pantalla de Auditoría (solo dueña, Task 3.4): historial de acciones sensibles, de solo lectura.
    public sealed class AuditView : UserControl, IRefreshable
    {
        // Mismo límite que AppData.GetAuditoria (LIMIT 5000).
        private const int RowLimit = 5000;
        private const int ActionColumn = 2;

        private readonly DateTimePicker fromPicker;
        private readonly DateTimePicker toPicker;
        private readonly EdenComboBox userCombo;
        private readonly Banner messageBanner;
        private readonly Banner limitBanner;
        private readonly Label countLabel;
        private readonly DataGridView grid = ReportUi.Grid();
        private readonly EmptyState empty = ReportUi.Empty("Sin acciones registradas", "No hay acciones registradas con estos filtros. Pruebe con otro rango de fechas o usuario.");
        private bool suppressReload = true;

        private sealed class UserItem
        {
            public string Id;
            public string Name;
            public override string ToString() { return Name; }
        }

        public AuditView()
        {
            BackColor = Theme.Canvas;
            Font = Theme.Body;
            Theme.EnableDoubleBuffer(this);

            var today = DateTime.Today;
            fromPicker = ReportUi.Picker(new DateTime(today.Year, today.Month, 1));
            toPicker = ReportUi.Picker(today);
            fromPicker.ValueChanged += (s, e) => Reload();
            toPicker.ValueChanged += (s, e) => Reload();

            userCombo = Theme.Combo();
            userCombo.Width = 190;
            userCombo.Margin = new Padding(0);
            userCombo.SelectedIndexChanged += (s, e) => Reload();

            // Cabecera: título y, en la zona de acciones, los filtros (el primero añadido queda a la derecha).
            var header = new ScreenHeader { Title = "Auditoría", Subtitle = "Acciones sensibles. Solo lectura." };
            header.AddAction(ReportUi.Labeled("Usuario", userCombo));
            header.AddAction(ReportUi.Labeled("Hasta", Theme.Field(toPicker)));
            header.AddAction(ReportUi.Labeled("Desde", Theme.Field(fromPicker)));

            // Cuerpo: atajos de rango, avisos y tabla.
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Md, Theme.Space.Xxl - Theme.Space.Lg, Theme.Space.Md) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Canvas };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (var i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            body.Controls.Add(root);
            Controls.Add(body);
            Controls.Add(header);

            var shortcuts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Canvas, Margin = new Padding(0, 0, 0, Theme.Space.Sm) };
            AddShortcut(shortcuts, "Hoy", () => SetRange(DateTime.Today, DateTime.Today));
            AddShortcut(shortcuts, "Esta semana", () => SetRange(ReportUi.WeekStart(DateTime.Today), DateTime.Today));
            AddShortcut(shortcuts, "Este mes", () => SetRange(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today));
            root.Controls.Add(shortcuts, 0, 0);

            messageBanner = Theme.Banner("", Tone.Danger);
            messageBanner.Dock = DockStyle.Fill;
            messageBanner.Margin = new Padding(0, 0, Theme.Space.Lg, Theme.Space.Md);
            messageBanner.Visible = false;
            root.Controls.Add(messageBanner, 0, 1);

            limitBanner = Theme.Banner("", Tone.Warning);
            limitBanner.Dock = DockStyle.Fill;
            limitBanner.Margin = new Padding(0, 0, Theme.Space.Lg, Theme.Space.Md);
            limitBanner.Visible = false;
            root.Controls.Add(limitBanner, 0, 2);

            countLabel = Theme.Label("");
            countLabel.Font = Theme.Caption;
            countLabel.ForeColor = Theme.Muted;
            countLabel.Margin = new Padding(0, Theme.Space.Xs, 0, 0);

            ReportUi.Column(grid, "Fecha", false, 1.1f);
            ReportUi.Column(grid, "Usuario", false, 1f);
            ReportUi.Column(grid, "Acción", false, 2f);
            ReportUi.Column(grid, "Detalle", false, 3f);
            // Alto de fila fijo (calcular el alto de miles de filas con AllCells es lento); el texto completo va en el tooltip del Detalle.
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.CellPainting += PaintActionPill;
            var block = ReportUi.Block("Acciones registradas", "Eliminaciones, reembolsos, cambios de precio, diferencias de caja, bloqueos de cuenta y cierres de sesión por inactividad. Solo lectura.", grid, empty);
            var blockHeader = block.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            if (blockHeader != null) blockHeader.Controls.Add(countLabel);
            root.Controls.Add(block, 0, 3);

            suppressReload = false;
            LoadUsers();
        }

        // Tono de la acción según su gravedad: eliminaciones y rechazos = Danger; cambios = Warning; restauraciones e importaciones = Info.
        internal static Tone ActionTone(string action)
        {
            switch (action)
            {
                case AuditActions.ProductoEliminado:
                case AuditActions.UsuarioEliminado:
                case AuditActions.ComprobanteRechazado:
                case AuditActions.LlavePerdida:
                case AuditActions.ImplementoPerdido:
                    return Tone.Danger;
                case AuditActions.ReciboReembolsado:
                case AuditActions.CajaDiferencia:
                case AuditActions.UsuarioDesactivado:
                case AuditActions.PrecioCambiado:
                case AuditActions.InventarioAjustado:
                case AuditActions.CuentaBloqueada:
                case AuditActions.CargoPerdidaCambiado:
                    return Tone.Warning;
                case AuditActions.CopiaRestaurada:
                case AuditActions.DatosImportados:
                case AuditActions.CuentaDesbloqueada:
                case AuditActions.LlaveLiberada:
                    return Tone.Info;
                default:
                    return Tone.Neutral;
            }
        }

        // La acción se dibuja como Pill dentro de la celda (el texto de la celda sigue siendo el nombre legible).
        private void PaintActionPill(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ActionColumn) return;
            var code = grid.Rows[e.RowIndex].Tag as string;
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            e.Handled = true;
            var text = Convert.ToString(e.FormattedValue, CultureInfo.CurrentCulture) ?? "";
            if (text.Length == 0) return;
            var tone = ActionTone(code);
            var measured = TextRenderer.MeasureText(text, Theme.FieldLabel, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            var room = Math.Max(20, e.CellBounds.Width - Theme.Space.Sm * 2);
            var width = Math.Min(room, measured + Theme.Space.Md * 2);
            var rect = new Rectangle(e.CellBounds.Left + Theme.Space.Sm, e.CellBounds.Top + (e.CellBounds.Height - Theme.Dim.Pill) / 2, width, Theme.Dim.Pill);
            var g = e.Graphics;
            var previous = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Gfx.Round(rect, rect.Height / 2))
            using (var brush = new SolidBrush(Tones.Soft(tone))) g.FillPath(brush, path);
            g.SmoothingMode = previous;
            TextRenderer.DrawText(g, text, Theme.FieldLabel, new Rectangle(rect.Left + Theme.Space.Md, rect.Top, Math.Max(0, rect.Width - Theme.Space.Md * 2), rect.Height), Tones.Strong(tone),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void AddShortcut(FlowLayoutPanel parent, string text, Action action)
        {
            var button = ReportUi.Shortcut(text, action);
            button.Width = button.GetPreferredSize(Size.Empty).Width;
            parent.Controls.Add(button);
        }

        // Rellena el combo ("Todos" + usuarios) conservando la selección y recarga el grid.
        private void LoadUsers()
        {
            var selected = userCombo.SelectedItem as UserItem;
            suppressReload = true;
            userCombo.Items.Clear();
            userCombo.Items.Add(new UserItem { Id = null, Name = "Todos" });
            try
            {
                foreach (var user in AppData.Users.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    userCombo.Items.Add(new UserItem { Id = user.Id, Name = user.Name });
            }
            catch (MySqlException ex)
            {
                AppData.LogError(ex, "Auditoría");
                ShowMessage(AppData.DescribeDatabaseError(ex));
            }
            var index = 0;
            if (selected != null)
                for (var i = 0; i < userCombo.Items.Count; i++)
                    if (((UserItem)userCombo.Items[i]).Id == selected.Id) index = i;
            userCombo.SelectedIndex = index;
            suppressReload = false;
            Reload();
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
            LoadUsers();
        }

        private void ShowMessage(string text)
        {
            messageBanner.Text = text ?? "";
            messageBanner.Visible = !string.IsNullOrEmpty(text);
        }

        // Nombre legible de la acción; un código desconocido se muestra tal cual.
        internal static string ActionName(string action)
        {
            switch (action)
            {
                case AuditActions.ProductoEliminado: return "Producto eliminado";
                case AuditActions.ReciboReembolsado: return "Recibo reembolsado";
                case AuditActions.CajaDiferencia: return "Diferencia de caja";
                case AuditActions.UsuarioEliminado: return "Usuario eliminado";
                case AuditActions.UsuarioDesactivado: return "Usuario desactivado";
                case AuditActions.PrecioCambiado: return "Precio cambiado";
                case AuditActions.CopiaRestaurada: return "Copia restaurada";
                case AuditActions.DatosImportados: return "Datos importados";
                case AuditActions.ComprobanteRechazado: return "Comprobante rechazado (simulado)";
                case AuditActions.CuentaBloqueada: return "Cuenta bloqueada";
                case AuditActions.CuentaDesbloqueada: return "Cuenta desbloqueada";
                case AuditActions.SesionInactividad: return "Sesión cerrada por inactividad";
                case AuditActions.InventarioAjustado: return "Inventario ajustado";
                case AuditActions.LlavePerdida: return "Llave no devuelta";
                case AuditActions.ImplementoPerdido: return "Implemento no devuelto";
                case AuditActions.LlaveLiberada: return "Llave liberada";
                case AuditActions.CargoPerdidaCambiado: return "Cargo por pérdida cambiado";
                default: return action;
            }
        }

        private void Reload()
        {
            if (suppressReload) return;
            var from = fromPicker.Value.Date;
            var to = toPicker.Value.Date;
            if (from > to)
            {
                ShowMessage("La fecha \"Desde\" no puede ser posterior a \"Hasta\". Ajuste el rango para ver la auditoría.");
                return;
            }
            var user = userCombo.SelectedItem as UserItem;
            List<AuditEntry> entries;
            try
            {
                entries = AppData.GetAuditoria(from, to, user == null ? null : user.Id);
            }
            catch (MySqlException ex)
            {
                AppData.LogError(ex, "Auditoría");
                ShowMessage(AppData.DescribeDatabaseError(ex));
                return;
            }
            ShowMessage(null);

            grid.Rows.Clear();
            foreach (var entry in entries)
            {
                var rowIndex = grid.Rows.Add(entry.Date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), entry.User, ActionName(entry.Action), entry.Detail);
                grid.Rows[rowIndex].Tag = entry.Action;
                grid.Rows[rowIndex].Cells[3].ToolTipText = entry.Detail;
            }
            ReportUi.Show(grid, empty, entries.Count > 0);
            grid.ClearSelection();

            countLabel.Text = entries.Count + (entries.Count == 1 ? " acción en el rango" : " acciones en el rango");
            limitBanner.Text = "Se muestran las " + RowLimit + " acciones más recientes; hay más en este rango. Reduzca las fechas o elija un usuario para ver el resto.";
            limitBanner.Visible = entries.Count >= RowLimit;
        }
    }
}
