using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    // Usuarios y permisos: tabla con rol y estado como Pill; acciones agrupadas (Agregar principal, Editar/Desactivar secundarias, Eliminar destructiva).
    public sealed class UserManagementPanel : CardPanel
    {
        private readonly DataGridView grid;
        private readonly Button toggleButton;
        private readonly Button editButton;
        private readonly Button unlockButton;
        private readonly Button deleteButton;

        public UserManagementPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(760, 510);
            Padding = new Padding(Theme.Space.Xl);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label { Text = "Usuarios y permisos", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Margin = Padding.Empty };
            var subtitle = new Label { Text = "La dueña administra Inventario y Configuración. Los trabajadores usan Ventas, Recibos y Turno.", Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, Theme.Space.Xs, 0, 0) };
            var note = new Label { Text = "La cuenta inicial es Dueña con PIN 0000. Cambie ese PIN desde Editar cuando termine de configurar a los trabajadores.", Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, Theme.Space.Md, 0, 0) };

            var add = Theme.Button("Agregar usuario", ButtonVariant.Primary);
            add.Width = 168; add.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            add.Click += (s, e) => AddUser();
            editButton = Theme.Button("Editar", ButtonVariant.Secondary);
            editButton.Width = 100; editButton.Margin = new Padding(0, 0, Theme.Space.Sm, 0);
            editButton.Click += (s, e) => EditSelected();
            toggleButton = Theme.Button("Desactivar", ButtonVariant.Secondary);
            toggleButton.Width = 132; toggleButton.Margin = Padding.Empty;
            toggleButton.Click += (s, e) => ToggleSelected();
            unlockButton = Theme.Button("Desbloquear", ButtonVariant.Secondary);
            unlockButton.Width = 132; unlockButton.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            unlockButton.Visible = false;   // solo aparece con una cuenta bloqueada seleccionada (RF-01)
            unlockButton.Click += (s, e) => UnlockSelected();
            deleteButton = Theme.Button("Eliminar", ButtonVariant.Destructive);
            deleteButton.Width = 112; deleteButton.Dock = DockStyle.Right; deleteButton.Margin = Padding.Empty;
            deleteButton.Click += (s, e) => DeleteSelected();

            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
            left.Controls.Add(add); left.Controls.Add(editButton); left.Controls.Add(toggleButton); left.Controls.Add(unlockButton);
            var toolbar = new Panel { Dock = DockStyle.Fill, Height = Theme.Dim.Control, BackColor = Theme.Surface, Margin = new Padding(0, Theme.Space.Lg, 0, Theme.Space.Md) };
            toolbar.Controls.Add(left);
            toolbar.Controls.Add(deleteButton);

            grid = new DataGridView { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Nombre", FillWeight = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Role", HeaderText = "Rol", Width = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Estado", Width = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.SelectionChanged += (s, e) => UpdateActions();
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSelected(); };
            grid.CellPainting += PaintPills;

            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(subtitle, 0, 1);
            layout.Controls.Add(toolbar, 0, 2);
            layout.Controls.Add(grid, 0, 3);
            layout.Controls.Add(note, 0, 4);
            Controls.Add(layout);

            // Los textos largos se ajustan al ancho disponible.
            layout.SizeChanged += (s, e) =>
            {
                var w = Math.Max(200, layout.ClientSize.Width);
                subtitle.MaximumSize = new Size(w, 0);
                note.MaximumSize = new Size(w, 0);
            };
            RefreshUsers();
        }

        // Rol y estado se dibujan como insignias (Pill); el valor de la celda sigue siendo el texto.
        private void PaintPills(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != 1 && e.ColumnIndex != 2)) return;
            var user = grid.Rows[e.RowIndex].Tag as AppUser;
            if (user == null) return;
            e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            Tone tone; bool dot = false;
            if (e.ColumnIndex == 1) tone = user.Role == UserRoles.Owner ? Tone.Brand : Tone.Neutral;
            else if (UserSecurity.IsLocked(user) && user.Active) { tone = Tone.Warning; dot = true; }
            else { tone = user.Active ? Tone.Success : Tone.Neutral; dot = true; }
            var text = Convert.ToString(e.Value);
            var size = TextRenderer.MeasureText(text, Theme.FieldLabel, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var width = size.Width + Theme.Space.Md * 2 + (dot ? 14 : 0);
            var rect = new Rectangle(e.CellBounds.Left + Theme.Space.Sm, e.CellBounds.Top + (e.CellBounds.Height - Theme.Dim.Pill) / 2, Math.Min(width, e.CellBounds.Width - Theme.Space.Md), Theme.Dim.Pill);
            var g = e.Graphics;
            var previous = g.SmoothingMode;   // Graphics compartido por todas las celdas: se restaura al terminar
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Gfx.Round(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), rect.Height / 2))
            using (var brush = new SolidBrush(grid.Rows[e.RowIndex].Selected ? Theme.Surface : Tones.Soft(tone))) g.FillPath(brush, path);
            var left = rect.Left + Theme.Space.Md;
            if (dot)
            {
                using (var brush = new SolidBrush(Tones.Strong(tone))) g.FillEllipse(brush, left - 2, rect.Top + rect.Height / 2 - 3, 6, 6);
                left += 12;
            }
            g.SmoothingMode = previous;
            TextRenderer.DrawText(g, text, Theme.FieldLabel, new Rectangle(left - 2 + (dot ? 0 : 2), rect.Top, Math.Max(0, rect.Right - left - Theme.Space.Sm), rect.Height), Tones.Strong(tone),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            e.Handled = true;
        }

        public void RefreshUsers()
        {
            if (grid == null) return;
            var selectedId = Current() == null ? null : Current().Id;
            System.Collections.Generic.List<AppUser> users;
            try
            {
                users = AppData.Users;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Listar usuarios (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
                return;
            }
            grid.Rows.Clear();
            foreach (var user in users.OrderBy(x => x.Role == UserRoles.Owner ? 0 : 1).ThenBy(x => x.Name))
            {
                var row = grid.Rows.Add(user.Name, UserRoles.DisplayName(user.Role), !user.Active ? "Inactivo" : UserSecurity.IsLocked(user) ? "Bloqueada" : "Activo");
                grid.Rows[row].Tag = user;
                if (!user.Active) grid.Rows[row].DefaultCellStyle.ForeColor = Theme.Muted;
                if (user.Id == selectedId) grid.Rows[row].Selected = true;
            }
            if (grid.Rows.Count > 0 && grid.SelectedRows.Count == 0) grid.Rows[0].Selected = true;
            UpdateActions();
        }

        private AppUser Current()
        {
            return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as AppUser;
        }

        private void UpdateActions()
        {
            var user = Current();
            toggleButton.Enabled = user != null;
            toggleButton.Text = user != null && user.Active ? "Desactivar" : "Activar"; editButton.Enabled = user != null; deleteButton.Enabled = user != null;
            unlockButton.Visible = user != null && UserSecurity.IsLocked(user);
        }

        private void UnlockSelected()
        {
            var user = Current();
            if (user == null) return;
            try
            {
                AppData.UnlockUser(user.Id);
                RefreshUsers();
                Theme.Message(FindForm(), Tone.Success, "Cuenta de " + user.Name + " desbloqueada. Ya puede ingresar con su PIN.", "Usuarios");
            }
            catch (InvalidOperationException ex)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "Usuarios");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Desbloquear usuario (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
            }
        }

        private void AddUser()
        {
            using (var dialog = new UserEditorDialog(null))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    AppData.SaveUser(null, dialog.UserName, dialog.Pin, dialog.Role);
                    RefreshUsers();
                    Theme.Message(FindForm(), Tone.Success, "Usuario agregado correctamente.", "Usuarios");
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo agregar");
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Agregar usuario (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
                }
            }
        }

        private void EditSelected()
        {
            var user = Current();
            if (user == null) return;
            using (var dialog = new UserEditorDialog(user))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    AppData.SaveUser(user.Id, dialog.UserName, dialog.Pin, dialog.Role);
                    RefreshUsers();
                    Theme.Message(FindForm(), Tone.Success, user.Active && UserSecurity.IsLocked(user) && !string.IsNullOrWhiteSpace(dialog.Pin)
                        ? "Usuario actualizado. Al cambiar el PIN, la cuenta quedó desbloqueada."
                        : "Usuario actualizado. Los permisos se aplicarán completamente en el próximo inicio de sesión.", "Usuarios");
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    Theme.Message(FindForm(), Tone.Warning, ex.Message, "No se pudo actualizar");
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    AppData.LogError(ex, "Actualizar usuario (base de datos)");
                    Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
                }
            }
        }

        private void ToggleSelected()
        {
            var user = Current();
            if (user == null) return;
            try
            {
                AppData.SetUserActive(user.Id, !user.Active);
                RefreshUsers();
            }
            catch (InvalidOperationException ex)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "Usuarios");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Activar o desactivar usuario (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
            }
        }

        private void DeleteSelected()
        {
            var user = Current();
            if (user == null) return;
            if (MessageBox.Show(FindForm(), "¿Eliminar al usuario “" + user.Name + "”?", "Usuarios", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                AppData.DeleteUser(user.Id);
                RefreshUsers();
            }
            catch (InvalidOperationException ex)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "Usuarios");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Eliminar usuario (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Usuarios");
            }
        }
    }


    public sealed class UserEditorDialog : Form
    {
        private readonly TextBox nameBox;
        private readonly TextBox pinBox;
        private readonly EdenComboBox roleBox;
        private readonly Label nameError;
        private readonly Label pinError;
        private readonly bool editing;

        public string UserName { get { return nameBox.Text.Trim(); } }
        public string Pin { get { return pinBox.Text; } }
        public string Role { get { return roleBox.SelectedIndex == 0 ? UserRoles.Worker : UserRoles.Owner; } }

        public UserEditorDialog(AppUser user)
        {
            editing = user != null;
            Text = editing ? "Editar usuario" : "Agregar usuario";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(480, 500);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Surface;
            WindowChrome.Integrate(this);
            Font = Theme.Body;

            const int contentWidth = 432;
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Theme.Surface, Padding = new Padding(Theme.Space.Xl, Theme.Space.Md, Theme.Space.Xl, Theme.Space.Lg) };
            var save = Theme.Button("Guardar", ButtonVariant.Primary);
            save.Dock = DockStyle.Right; save.Width = 132;
            save.Click += Save_Click;
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.Space.Sm, BackColor = Theme.Surface };
            var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
            cancel.Dock = DockStyle.Right; cancel.Width = 120; cancel.DialogResult = DialogResult.Cancel;
            // Dock=Right se evalúa del último al primero añadido: Guardar queda a la derecha del todo.
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(gap);
            buttons.Controls.Add(save);
            var divider = Theme.Separator(); divider.Dock = DockStyle.Bottom;

            var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Xl, 0) };

            var title = new Label { Text = editing ? "Editar usuario" : "Nuevo usuario", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Margin = Padding.Empty };
            var subtitle = new Label { Text = editing ? "Cambie el nombre, el rol o el PIN de acceso." : "El PIN es lo que usará para iniciar sesión.", Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, Theme.Space.Xs, 0, Theme.Space.Lg) };
            stack.Controls.Add(title);
            stack.Controls.Add(subtitle);

            var nameLabel = Theme.LabelField("Nombre"); nameLabel.Margin = new Padding(0, 0, 0, Theme.Space.Xs);
            stack.Controls.Add(nameLabel);
            nameBox = new TextBox { Size = new Size(contentWidth, 28), MaxLength = 60, Text = user == null ? "" : user.Name };
            var nameField = Theme.Field(nameBox); nameField.Size = new Size(contentWidth, Theme.Dim.Field); nameField.Margin = Padding.Empty;
            stack.Controls.Add(nameField);
            nameError = ErrorLabel(contentWidth);
            stack.Controls.Add(nameError);

            var roleLabel = Theme.LabelField("Rol"); roleLabel.Margin = new Padding(0, Theme.Space.Sm, 0, Theme.Space.Xs);
            stack.Controls.Add(roleLabel);
            roleBox = Theme.Combo();
            roleBox.Size = new Size(240, Theme.Dim.Field); roleBox.Margin = Padding.Empty;
            roleBox.Items.Add("Trabajador");
            roleBox.Items.Add("Dueña");
            roleBox.SelectedIndex = user != null && user.Role == UserRoles.Owner ? 1 : 0;
            stack.Controls.Add(roleBox);

            var pinLabel = Theme.LabelField(editing ? "Nuevo PIN (vacío conserva el actual)" : "PIN de acceso"); pinLabel.Margin = new Padding(0, Theme.Space.Lg, 0, Theme.Space.Xs);
            stack.Controls.Add(pinLabel);
            var pinRow = new Panel { Size = new Size(contentWidth, Theme.Dim.Field), BackColor = Theme.Surface, Margin = Padding.Empty };
            pinBox = new TextBox { MaxLength = 8, UseSystemPasswordChar = true, Size = new Size(200, 28) };
            pinBox.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            var pinField = Theme.Field(pinBox); pinField.Dock = DockStyle.Fill;
            pinRow.Controls.Add(pinField);
            var show = Theme.Button("Mostrar", ButtonVariant.Secondary);
            show.Dock = DockStyle.Right; show.Width = 112; show.Margin = Padding.Empty;
            show.Click += (s, e) =>
            {
                pinBox.UseSystemPasswordChar = !pinBox.UseSystemPasswordChar;
                show.Text = pinBox.UseSystemPasswordChar ? "Mostrar" : "Ocultar";
            };
            pinRow.Controls.Add(show);
            stack.Controls.Add(pinRow);
            var hint = new Label { Text = "Use entre 4 y 8 dígitos.", Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, Theme.Space.Xs, 0, 0) };
            stack.Controls.Add(hint);
            pinError = ErrorLabel(contentWidth);
            stack.Controls.Add(pinError);

            nameBox.TextChanged += (s, e) => nameError.Text = "";
            pinBox.TextChanged += (s, e) => pinError.Text = "";

            Controls.Add(stack);
            Controls.Add(divider);
            Controls.Add(buttons);
            CancelButton = cancel;
            AcceptButton = save;
            Shown += (s, e) => nameBox.Focus();
            Theme.FitDialogToWorkingArea(this);
        }

        private static Label ErrorLabel(int width)
        {
            return new Label { Text = "", Font = Theme.Caption, ForeColor = Theme.Danger, AutoSize = false, Size = new Size(width, 20), Margin = new Padding(0, Theme.Space.Xs, 0, 0) };
        }

        // Validación en línea: el mensaje aparece bajo el campo.
        private void Save_Click(object sender, EventArgs e)
        {
            if (!Validation.IsValidRequiredText(nameBox.Text, 3, 60))
            {
                nameError.Text = "El nombre debe tener entre 3 y 60 caracteres.";
                nameBox.Focus();
                return;
            }
            if ((!editing || pinBox.TextLength > 0) && !UserSecurity.IsValidPin(pinBox.Text))
            {
                pinError.Text = "El PIN debe contener entre 4 y 8 dígitos.";
                pinBox.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
