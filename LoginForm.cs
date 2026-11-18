using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class LoginForm : Form
    {
        private const int DesignWidth = 560, DesignHeight = 732;

        private readonly ComboBox userBox;
        private readonly TextBox pinBox;
        private readonly Banner status;
        private readonly Button loginButton;

        public LoginForm()
        {
            Text = "Ingresar a Edén POS";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(DesignWidth, DesignHeight);
            MaximizeBox = false;
            MinimizeBox = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Canvas;
            Font = Theme.Body;
            KeyPreview = true;

            // Una sola tarjeta elevada sobre el lienzo: sin franja verde; la marca la pone el logo.
            const int cardWidth = 480, contentWidth = 384, contentLeft = 48;
            var card = new CardPanel
            {
                Location = new Point((DesignWidth - cardWidth) / 2, Theme.Space.Lg),
                Size = new Size(cardWidth, DesignHeight - Theme.Space.Lg * 2),
                Elevated = true
            };
            Controls.Add(card);

            var logo = new PictureBox
            {
                Location = new Point((cardWidth - 80) / 2, 24),
                Size = new Size(80, 80),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Theme.Surface
            };
            logo.Image = LoadLoginLogo();
            card.Controls.Add(logo);

            var storeName = new Label
            {
                Text = AppData.Settings.StoreName,
                Location = new Point(contentLeft, 110),
                Size = new Size(contentWidth, 32),
                ForeColor = Theme.Ink,
                Font = Theme.Heading,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                BackColor = Theme.Surface
            };
            card.Controls.Add(storeName);

            var subtitle = new Label
            {
                Text = "Elija su usuario e ingrese su PIN",
                Location = new Point(contentLeft, 144),
                Size = new Size(contentWidth, 22),
                ForeColor = Theme.Muted,
                Font = Theme.Body,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Theme.Surface
            };
            card.Controls.Add(subtitle);

            var userLabel = Theme.LabelField("Usuario");
            userLabel.Location = new Point(contentLeft, 180);
            card.Controls.Add(userLabel);
            var combo = Theme.Combo();
            combo.Bounds = new Rectangle(contentLeft, 202, contentWidth, Theme.Dim.Field);
            combo.DisplayMember = "Name";
            combo.DropDownHeight = 180;
            combo.Font = Theme.Subhead;
            combo.SelectedIndexChanged += (s, e) => { ClearPin(); ShowLockedStatus(); };
            userBox = combo;
            card.Controls.Add(userBox);

            var pinLabel = Theme.LabelField("PIN");
            pinLabel.Location = new Point(contentLeft, 254);
            card.Controls.Add(pinLabel);
            pinBox = new TextBox
            {
                Bounds = new Rectangle(contentLeft, 276, contentWidth, 48),
                Font = Theme.Title,
                TextAlign = HorizontalAlignment.Center,
                ReadOnly = true,
                UseSystemPasswordChar = true,
                MaxLength = 8,
                TabStop = false
            };
            var pinField = Theme.Field(pinBox, 52);
            pinField.Bounds = new Rectangle(contentLeft, 276, contentWidth, 52);
            card.Controls.Add(pinField);

            // Mensaje en línea con color semántico (oculto mientras no haya nada que decir).
            status = Theme.Banner("", Tone.Danger);
            status.AutoHeight = false;   // la altura la fija SetStatus, acotada para no tapar el teclado
            status.Bounds = new Rectangle(contentLeft, 334, contentWidth, 44);
            status.Visible = false;
            card.Controls.Add(status);

            var keypad = new TableLayoutPanel
            {
                Location = new Point(contentLeft, 388),
                Size = new Size(contentWidth, 4 * 50 + 3 * Theme.Space.Sm),
                ColumnCount = 3,
                RowCount = 4,
                BackColor = Theme.Surface,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            for (var column = 0; column < 3; column++) keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            for (var row = 0; row < 4; row++) keypad.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
            var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "C", "0", "←" };
            foreach (var key in keys)
            {
                var isDigit = key.All(char.IsDigit);
                var button = Theme.Button(key, isDigit ? ButtonVariant.Secondary : ButtonVariant.Ghost);
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(Theme.Space.Xs);
                button.Padding = new Padding(0);
                button.Font = isDigit ? Theme.Title : Theme.Subhead;
                if (!isDigit) button.ForeColor = Theme.Muted;
                button.TabStop = false;   // sin parada con Tab; además, tras cada pulsación el foco vuelve a "Ingresar" (Keypad_Click) para que Enter no repita el dígito
                button.Tag = key;
                button.Click += Keypad_Click;
                keypad.Controls.Add(button);
            }
            card.Controls.Add(keypad);

            loginButton = Theme.Button("Ingresar", ButtonVariant.Primary);
            loginButton.Bounds = new Rectangle(contentLeft + Theme.Space.Xs, keypad.Bottom + Theme.Space.Lg, contentWidth - Theme.Space.Xs * 2, 48);
            loginButton.Margin = new Padding(0);
            loginButton.Font = Theme.Subhead;
            loginButton.Click += (s, e) => PerformLogin();
            card.Controls.Add(loginButton);
            AcceptButton = loginButton;

            keypadPanel = keypad;
            ApplyMonitorScale();
            CaptureStatusLayout(card);
            LoadUsers();
            if (!string.IsNullOrEmpty(AppSession.LogoutNotice))
            {
                SetStatus(AppSession.LogoutNotice, Tone.Info);
                AppSession.LogoutNotice = null;
            }
            KeyPress += LoginForm_KeyPress;
            KeyDown += LoginForm_KeyDown;
            FormClosed += (s, e) =>
            {
                if (logo.Image != null) logo.Image.Dispose();
            };
        }

        private TableLayoutPanel keypadPanel;
        private int statusBaseHeight, statusSpare, keypadBaseTop, loginBaseTop;

        // Con la escala ya aplicada: guarda la posición base del teclado y cuánto hueco libre queda bajo "Ingresar" dentro de la tarjeta.
        private void CaptureStatusLayout(Control card)
        {
            statusBaseHeight = status.Height;
            keypadBaseTop = keypadPanel.Top;
            loginBaseTop = loginButton.Top;
            statusSpare = Math.Max(0, card.Height - loginButton.Bottom - Theme.Space.Lg);
        }

        private void SetStatus(string text, Tone tone = Tone.Danger)
        {
            status.Tone = tone;
            var height = statusBaseHeight;
            if (!string.IsNullOrEmpty(text) && statusBaseHeight > 0)
            {
                // Mismo cálculo que Banner: texto ajustado al ancho útil; la altura crece hasta el hueco libre y el resto se recorta.
                var textWidth = Math.Max(20, status.Width - 44 - Theme.Space.Lg);
                var textHeight = TextRenderer.MeasureText(text, status.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                height = Math.Min(statusBaseHeight + statusSpare, Math.Max(statusBaseHeight, textHeight + Theme.Space.Md * 2));
            }
            if (statusBaseHeight > 0)
            {
                var extra = height - statusBaseHeight;
                status.Height = height;
                keypadPanel.Top = keypadBaseTop + extra;
                loginButton.Top = loginBaseTop + extra;
            }
            status.Text = text;
            status.Visible = !string.IsNullOrEmpty(text);
        }

        private void LoadUsers()
        {
            userBox.Items.Clear();
            System.Collections.Generic.List<AppUser> users;
            try
            {
                users = AppData.Users;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Inicio de sesión: cargar usuarios (base de datos)");
                SetStatus(AppData.DescribeDatabaseError(ex));
                SetLoginEnabled(false);
                return;
            }
            foreach (var user in users.Where(x => x.Active)
                         .OrderBy(x => x.Role == UserRoles.Owner ? 0 : 1)
                         .ThenBy(x => x.Name))
                userBox.Items.Add(user);
            if (userBox.Items.Count > 0)
            {
                userBox.SelectedIndex = 0;
                ShowLockedStatus();   // si el primer usuario ya está bloqueado (SelectedIndexChanged no se dispara cuando el índice no cambia)
            }
            else
            {
                SetStatus("No existen usuarios activos.", Tone.Warning);
                SetLoginEnabled(false);
            }
        }

        private void Keypad_Click(object sender, EventArgs e)
        {
            var key = Convert.ToString(((Button)sender).Tag);
            if (key == "C") ClearPin();
            else if (key == "←") RemoveLastDigit();
            else AddDigit(key);
            // El botón pulsado conserva el foco y Enter lo repetiría: se devuelve a "Ingresar" (o al campo PIN si está bloqueado).
            if (loginButton.Enabled) loginButton.Focus();
            else pinBox.Focus();
        }

        private void LoginForm_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (char.IsDigit(e.KeyChar))
            {
                AddDigit(e.KeyChar.ToString());
                e.Handled = true;
            }
        }

        private void LoginForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Back)
            {
                RemoveLastDigit();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                ClearPin();
                e.SuppressKeyPress = true;
            }
        }

        private void AddDigit(string digit)
        {
            if (!loginButton.Enabled || pinBox.TextLength >= pinBox.MaxLength) return;
            pinBox.Text += digit;
            SetStatus("");
        }

        private void RemoveLastDigit()
        {
            if (pinBox.TextLength > 0) pinBox.Text = pinBox.Text.Substring(0, pinBox.TextLength - 1);
            SetStatus("");
        }

        private void ClearPin()
        {
            pinBox.Clear();
            SetStatus("");
        }

        private void PerformLogin()
        {
            var selected = userBox.SelectedItem as AppUser;
            if (selected == null)
            {
                SetStatus("Seleccione un usuario.", Tone.Warning);
                return;
            }
            if (!UserSecurity.IsValidPin(pinBox.Text))
            {
                SetStatus("Ingrese un PIN de 4 a 8 dígitos.", Tone.Warning);
                return;
            }

            AppData.LoginResult result;
            try
            {
                // RF-01: el contador de intentos y el bloqueo viven en la base (valen para todas las cajas); con la cuenta bloqueada no se comprueba el PIN.
                result = AppData.TryLogin(selected.Id, pinBox.Text);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Inicio de sesión (base de datos)");
                ClearPin();
                SetStatus(AppData.DescribeDatabaseError(ex));
                return;
            }
            if (result.Outcome != AppData.LoginOutcome.Success)
            {
                ClearPin();
                if (result.Outcome == AppData.LoginOutcome.Locked || result.Outcome == AppData.LoginOutcome.JustLocked)
                {
                    selected.FailedAttempts = result.User.FailedAttempts;
                    selected.LockedSeconds = result.User.LockedSeconds;
                    SetStatus(UserSecurity.LockedMessage(result.User), Tone.Warning);
                }
                else if (result.AttemptsLeft > 0 && result.AttemptsLeft < UserSecurity.MaxFailedAttempts && result.User != null && result.User.FailedAttempts >= 2)
                    SetStatus("PIN incorrecto. Le " + (result.AttemptsLeft == 1 ? "queda 1 intento" : "quedan " + result.AttemptsLeft + " intentos") + " antes de que se bloquee la cuenta.");
                else SetStatus("PIN incorrecto. Inténtelo de nuevo.");
                return;
            }

            AppSession.SignIn(result.User);
            DialogResult = DialogResult.OK;
            Close();
        }

        // Al elegir un usuario cuya cuenta ya figura bloqueada, se avisa de inmediato (la comprobación definitiva la hace TryLogin).
        private void ShowLockedStatus()
        {
            var selected = userBox.SelectedItem as AppUser;
            if (selected != null && UserSecurity.IsLocked(selected)) SetStatus(UserSecurity.LockedMessage(selected), Tone.Warning);
        }

        private void SetLoginEnabled(bool enabled)
        {
            loginButton.Enabled = enabled;
            userBox.Enabled = enabled;
            foreach (Button button in Controls.OfType<Control>()
                         .SelectMany(Descendants)
                         .OfType<Button>()
                         .Where(x => x != loginButton))
                button.Enabled = enabled;
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        private static Image LoadLoginLogo()
        {
            string path;
            try
            {
                // Assembly.Location puede venir vacío (ensamblado cargado desde memoria); se usa la carpeta base como respaldo.
                var location = typeof(LoginForm).Assembly.Location;
                var assemblyDirectory = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
                if (string.IsNullOrEmpty(assemblyDirectory)) assemblyDirectory = AppDomain.CurrentDomain.BaseDirectory;
                path = Path.Combine(assemblyDirectory, "Assets", "EdenPOS-Login-v2.png");
            }
            catch
            {
                return null;
            }
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = File.OpenRead(path))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
            catch
            {
                return null;
            }
        }

        private void ApplyMonitorScale()
        {
            var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            var availableWidth = Math.Max(420, workingArea.Width - 48);
            var availableHeight = Math.Max(520, workingArea.Height - 32);
            var dpiScale = Math.Max(1f, DeviceDpi / 96f);
            var maximumScale = Math.Min(1.6f, Math.Max(1.25f, dpiScale));
            var scale = Math.Min(availableWidth / (float)DesignWidth, availableHeight / (float)DesignHeight);
            scale = Math.Max(0.55f, Math.Min(maximumScale, scale));
            if (Math.Abs(scale - 1f) > 0.01f)
            {
                ScaleControlTree(this, scale);
                ClientSize = new Size((int)Math.Round(DesignWidth * scale), (int)Math.Round(DesignHeight * scale));
            }

            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
        }

        private static void ScaleControlTree(Control parent, float scale)
        {
            foreach (Control child in parent.Controls)
            {
                if (child.Dock == DockStyle.None)
                {
                    child.Bounds = new Rectangle(
                        (int)Math.Round(child.Left * scale),
                        (int)Math.Round(child.Top * scale),
                        Math.Max(1, (int)Math.Round(child.Width * scale)),
                        Math.Max(1, (int)Math.Round(child.Height * scale)));
                }
                else if (child.Dock == DockStyle.Top || child.Dock == DockStyle.Bottom)
                {
                    child.Height = Math.Max(1, (int)Math.Round(child.Height * scale));
                }
                else if (child.Dock == DockStyle.Left || child.Dock == DockStyle.Right)
                {
                    child.Width = Math.Max(1, (int)Math.Round(child.Width * scale));
                }

                child.Margin = ScalePadding(child.Margin, scale);
                child.Padding = ScalePadding(child.Padding, scale);
                if (child.Font != null)
                    child.Font = new Font(child.Font.FontFamily, Math.Max(7f, child.Font.Size * scale), child.Font.Style, GraphicsUnit.Point);
                ScaleControlTree(child, scale);
            }
        }

        private static Padding ScalePadding(Padding value, float scale)
        {
            return new Padding(
                (int)Math.Round(value.Left * scale),
                (int)Math.Round(value.Top * scale),
                (int)Math.Round(value.Right * scale),
                (int)Math.Round(value.Bottom * scale));
        }
    }
}
