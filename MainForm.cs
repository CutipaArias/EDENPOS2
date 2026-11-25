using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class MainForm : Form
    {
        private readonly Panel sidebar;
        private readonly Panel host;
        private readonly Label employeeLabel;
        private readonly Label storeLabel;
        private readonly Pill rolePill;
        private readonly Dictionary<string, NavButton> navButtons = new Dictionary<string, NavButton>();
        private readonly Dictionary<string, Control> views = new Dictionary<string, Control>();
        private string currentKey;
        public bool LogoutRequested { get; private set; }
        private DataRevisionPoller revisionPoller;   // sondeo en segundo plano de cambios hechos por otras cajas
        private bool remoteRefreshQueued;

        // RNF-03: sin teclado ni ratón durante este tiempo se cierra la sesión. Cambie solo este valor para ajustarlo.
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
        private const int IdleCheckIntervalMs = 5000;
        private readonly Timer idleTimer;
        private ActivityFilter activityFilter;
        private int idleCloseAttempts;   // comprobaciones seguidas que han intentado cerrar ventanas abiertas sin conseguirlo
        private const int IdleMaxCloseAttempts = 3;   // pasado este tope se cierra la sesión aunque quede alguna ventana
        private DateTime lastActivityUtc = DateTime.UtcNow;   // reloj de pared: tras suspender el equipo también cuenta

        // Cierra la ventana principal y vuelve al inicio de sesión (p. ej. si una restauración eliminó al usuario de la sesión).
        public void ForceLogout()
        {
            LogoutRequested = true;
            Close();
        }

        // Barra de título verde oscuro (el color de la barra lateral), sin título ni ícono dibujados (ver WindowChrome).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowChrome.Blend(this, Theme.SidebarBack);
        }

        public MainForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = "Edén POS";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(800, 600);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Canvas;
            Font = Theme.Body;
            KeyPreview = true;

            // El área de contenido es el lienzo (Canvas): cada vista pone su ScreenHeader encima, sin franja verde propia de MainForm.
            sidebar = new Panel { Dock = DockStyle.Left, Width = 232, BackColor = Theme.SidebarBack, Padding = new Padding(0) };
            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
            Controls.Add(host);
            Controls.Add(sidebar);

            var sidebarLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Theme.SidebarBack,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            sidebarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 136f));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));
            sidebar.Controls.Add(sidebarLayout);

            // Identidad: negocio arriba, debajo el usuario y su rol en una Pill.
            var brand = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SidebarBack, Padding = new Padding(Theme.Space.Xl, Theme.Space.Xl, Theme.Space.Lg, Theme.Space.Md), Margin = new Padding(0) };
            storeLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = SidebarWhite,
                Font = Theme.Subhead,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                BackColor = Theme.SidebarBack
            };
            employeeLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Theme.SidebarText,
                Font = Theme.Body,
                TextAlign = ContentAlignment.BottomLeft,
                AutoEllipsis = true,
                BackColor = Theme.SidebarBack
            };
            var roleHost = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.SidebarBack, Padding = new Padding(0, Theme.Space.Sm, 0, 0) };
            rolePill = Theme.Pill("", Tone.Brand);
            rolePill.Location = new Point(0, Theme.Space.Sm);
            roleHost.Controls.Add(rolePill);
            brand.Controls.Add(roleHost);
            brand.Controls.Add(employeeLabel);
            brand.Controls.Add(storeLabel);
            sidebarLayout.Controls.Add(brand, 0, 0);

            // Pie: versión y "Cerrar sesión" como acción secundaria.
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.SidebarBack,
                Margin = new Padding(0),
                Padding = new Padding(Theme.Space.Lg, Theme.Space.Xs, Theme.Space.Lg, Theme.Space.Lg)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            footer.Controls.Add(new Label
            {
                Text = "Edén POS  ·  v2.3.1",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.SidebarMuted,
                Font = Theme.Caption,
                BackColor = Theme.SidebarBack
            }, 0, 0);
            var logout = Theme.Button("Cerrar sesión", ButtonVariant.Ghost);
            logout.Dock = DockStyle.Fill;
            logout.Margin = new Padding(0);
            logout.ForeColor = Theme.SidebarText;
            logout.FlatAppearance.BorderColor = Theme.SidebarActive;
            logout.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
            logout.FlatAppearance.MouseDownBackColor = Theme.SidebarActive;
            logout.Click += (s, e) =>
            {
                if (MessageBox.Show("¿Cerrar la sesión de " + AppSession.UserName + "?", "Cerrar sesión", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                SalesView.DiscardIdleDraft();
                LogoutRequested = true;
                Close();
            };
            footer.Controls.Add(logout, 0, 1);
            sidebarLayout.Controls.Add(footer, 0, 2);

            var navigation = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.SidebarBack,
                Padding = new Padding(Theme.Space.Md, Theme.Space.Sm, Theme.Space.Md, 0)
            };
            sidebarLayout.Controls.Add(navigation, 0, 1);

            if (AppSession.IsOwner)
            {
                AddNavigation(navigation, "inventory", "", "Inventario", null, () => new ProductsView());
                AddNavigation(navigation, "reports", "", "Reportes", null, () => new ReportsView());
                AddNavigation(navigation, "audit", "", "Auditoría", null, () => new AuditView());
                AddNavigation(navigation, "settings", "", "Configuración", null, () => new SettingsView());
            }
            else
            {
                AddNavigation(navigation, "sales", "", "Ventas", "F2", () => new SalesView());
                AddNavigation(navigation, "receipts", "", "Recibos", "F3", () => new ReceiptsView());
                AddNavigation(navigation, "shift", "", "Turno", "F4", () => new ShiftView());
            }

            var updatingLayout = false;
            Action updateResponsiveLayout = () =>
            {
                if (updatingLayout) return;
                updatingLayout = true;
                try
                {
                    var dpiScale = Math.Max(1f, DeviceDpi / 96f);
                    var minimumSidebar = (int)Math.Round(180 * dpiScale);
                    var maximumSidebar = (int)Math.Round(300 * dpiScale);
                    sidebar.Width = Math.Max(minimumSidebar, Math.Min(maximumSidebar, (int)(ClientSize.Width * 0.17)));
                    // El cambio de ancho ocurre dentro del layout de la ventana y WinForms aplaza el de la barra: sin esto el
                    // contenido de la barra se quedaba con el ancho anterior (botones más estrechos que la barra).
                    sidebar.PerformLayout();
                    sidebarLayout.PerformLayout();
                    foreach (var button in navButtons.Values)
                        button.Width = Math.Max(150, navigation.ClientSize.Width - navigation.Padding.Horizontal);
                }
                finally { updatingLayout = false; }
            };
            Resize += (s, e) => updateResponsiveLayout();
            navigation.SizeChanged += (s, e) => updateResponsiveLayout();

            AppData.DataChanged += OnDataChanged;
            revisionPoller = new DataRevisionPoller(OnRemoteRevisionChanged, 5000);
            // Detección de actividad en toda la aplicación (también dentro de diálogos) y comprobación periódica.
            activityFilter = new ActivityFilter(() => lastActivityUtc = DateTime.UtcNow);
            Application.AddMessageFilter(activityFilter);
            idleTimer = new Timer { Interval = IdleCheckIntervalMs };
            idleTimer.Tick += (s, e) => CheckIdle();
            idleTimer.Start();
            FormClosed += (s, e) =>
            {
                idleTimer.Stop();
                idleTimer.Dispose();
                if (activityFilter != null) Application.RemoveMessageFilter(activityFilter);
                activityFilter = null;
                AppData.DataChanged -= OnDataChanged;
                var poller = revisionPoller;
                revisionPoller = null;
                if (poller != null) poller.Dispose();
                host.Controls.Clear();
                foreach (var view in views.Values.ToList()) view.Dispose();   // todas las vistas cacheadas (se desuscriben de AppData)
                views.Clear();
            };
            KeyDown += MainForm_KeyDown;
            UpdateIdentity();
            updateResponsiveLayout();
            Navigate(AppSession.IsOwner ? "inventory" : "sales");
        }

        // ---- Cierre de sesión por inactividad (RNF-03) ----

        // Se llama cada IdleCheckIntervalMs (y desde las pruebas con una hora simulada). Reglas:
        //  - Cobrar (CheckoutDialog) o recordatorio de alquiler abiertos: se espera a que se cierren, con un tope de 2 x IdleTimeout sin actividad.
        //    Pasado el tope se tratan como cualquier otro diálogo (Cancelar no cobra nada y el carrito se conserva).
        //  - Cualquier otro diálogo (incluida la venta completada: la venta ya está guardada) o aviso se cierra como "Cancelar" y la sesión se
        //    cierra en la siguiente comprobación. Si tras IdleMaxCloseAttempts intentos aún queda alguna ventana que no se deja cerrar, se cierra igualmente.
        //  - Sin diálogos: se registra en auditoría (en segundo plano), se conserva el carrito en memoria y se vuelve al inicio de sesión.
        internal void CheckIdle(DateTime? nowUtc = null)
        {
            if (LogoutRequested || IsDisposed || !IsHandleCreated) return;
            var idle = (nowUtc ?? DateTime.UtcNow) - lastActivityUtc;
            if (idle < IdleTimeout) { idleCloseAttempts = 0; return; }
            if (AppSession.CurrentUser == null) return;

            if (idle < IdleTimeout + IdleTimeout)
                foreach (Form form in Application.OpenForms.Cast<Form>().ToList())
                    if (form != this && form.Visible && form.Modal && (form is CheckoutDialog || form is RentalReminderDialog)) return;   // cobro en curso: se espera

            var closedSomething = false;
            foreach (Form form in Application.OpenForms.Cast<Form>().ToList())
            {
                if (form == this || !form.Visible || !form.Modal) continue;
                form.DialogResult = DialogResult.Cancel;
                closedSomething = true;
            }
            if (CloseNativeDialogs(idleCloseAttempts > 0)) closedSomething = true;
            if (closedSomething && idleCloseAttempts < IdleMaxCloseAttempts)
            {
                idleCloseAttempts++;
                return;   // la sesión se cierra en la siguiente comprobación, cuando los diálogos ya terminaron
            }
            // Sin diálogos, o alguno se niega a cerrarse tras varios intentos (p. ej. un diálogo de archivos): se cierra la sesión igualmente.

            var user = AppSession.CurrentUser;
            var minutes = (int)Math.Round(IdleTimeout.TotalMinutes);
            var userId = user.Id;
            var userName = user.Name;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { AppData.LogIdleLogout(userId, userName, minutes); }
                catch (Exception ex) { AppData.LogError(ex, "Auditoría de cierre de sesión por inactividad"); }
            });
            Control salesView;
            var sales = views.TryGetValue("sales", out salesView) ? salesView as SalesView : null;
            if (sales != null) sales.StashIdleDraft();
            AppSession.LogoutNotice = "Sesión cerrada por inactividad. Ingrese de nuevo.";
            ForceLogout();
        }

        private delegate bool EnumThreadWindowsProc(IntPtr hwnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EndDialog(IntPtr hwnd, IntPtr result);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111;
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // MessageBox.Show (confirmaciones y avisos) y los diálogos de archivos no son Form de WinForms: son ventanas nativas (#32770) de este mismo hilo.
        // Primero se les pide cerrar con WM_COMMAND/IDCANCEL (2) y WM_CLOSE; en los intentos siguientes también EndDialog (los MessageBox Sí/No
        // ignoran WM_CLOSE; IDCANCEL equivale a no confirmar). Los diálogos de archivos pueden no obedecer: por eso CheckIdle tiene un tope de intentos.
        private static bool CloseNativeDialogs(bool forceEnd)
        {
            var found = false;
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, lParam) =>
            {
                var name = new System.Text.StringBuilder(32);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() != "#32770" || !IsWindowVisible(hwnd)) return true;
                found = true;
                PostMessage(hwnd, WM_COMMAND, (IntPtr)2, IntPtr.Zero);
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (forceEnd) EndDialog(hwnd, (IntPtr)2);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Marca actividad con teclas, clics, rueda y movimiento real del ratón (WM_MOUSEMOVE se repite sin que se mueva el ratón al repintar,
        // por eso se compara la posición del cursor).
        // Limitación aceptada: dentro de los bucles de mensajes nativos (MessageBox y diálogos de archivos) el filtro no recibe los mensajes,
        // así que la actividad en esos diálogos no reinicia el contador de inactividad.
        private sealed class ActivityFilter : IMessageFilter
        {
            private readonly Action onActivity;
            private Point lastCursor = Cursor.Position;

            public ActivityFilter(Action onActivity) { this.onActivity = onActivity; }

            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0100: case 0x0101: case 0x0104: case 0x0105:   // WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP
                    case 0x0201: case 0x0203: case 0x0204: case 0x0207:   // WM_LBUTTONDOWN, WM_LBUTTONDBLCLK, WM_RBUTTONDOWN, WM_MBUTTONDOWN
                    case 0x020A:                                          // WM_MOUSEWHEEL
                    case 0x00A1:                                          // WM_NCLBUTTONDOWN (barra de título, bordes)
                        onActivity();
                        break;
                    case 0x0200:                                          // WM_MOUSEMOVE
                        var position = Cursor.Position;
                        if (position != lastCursor) { lastCursor = position; onActivity(); }
                        break;
                }
                return false;
            }
        }

        private static readonly Color SidebarWhite = Color.White;   // blanco sobre SidebarBack

        private void AddNavigation(FlowLayoutPanel navigation, string key, string glyph, string text, string hint, Func<Control> factory)
        {
            var button = new NavButton(glyph, text, hint) { Width = 200, Tag = factory };
            button.Click += (s, e) => Navigate(key);
            navigation.Controls.Add(button);
            navButtons[key] = button;
        }

        private void Navigate(string key)
        {
            if (!navButtons.ContainsKey(key)) return;
            currentKey = key;
            foreach (var item in navButtons) item.Value.Active = item.Key == key;

            if (!views.ContainsKey(key))
            {
                var factory = (Func<Control>)navButtons[key].Tag;
                var control = factory();
                control.Dock = DockStyle.Fill;
                views[key] = control;
            }

            host.SuspendLayout();
            host.Controls.Clear();
            host.Controls.Add(views[key]);
            host.ResumeLayout();
            var refreshable = views[key] as IRefreshable;
            if (refreshable != null) refreshable.RefreshView();
        }

        private void UpdateIdentity(bool automatic = false)
        {
            AppSettings settings;
            try
            {
                settings = AppData.Settings;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Identidad (base de datos)");
                if (automatic) return;   // refresco automático: se registra y se sigue, sin ventana modal
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex));
                return;
            }
            employeeLabel.Text = string.IsNullOrWhiteSpace(AppSession.UserName) ? settings.EmployeeName : AppSession.UserName;
            rolePill.Text = AppSession.RoleName;
            storeLabel.Text = settings.StoreName;
            Text = "Edén POS — " + settings.StoreName;
        }

        private void OnDataChanged(object sender, EventArgs e)
        {
            var poller = revisionPoller;
            if (poller != null) poller.NotifyLocalChange();   // cambio de esta caja: no se vuelve a refrescar al detectarlo el sondeo
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(UpdateCurrentView)); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            UpdateCurrentView();
        }

        // Hilo de fondo: otra caja cambió datos. Solo se pasa a la UI; nada de MySQL aquí.
        private void OnRemoteRevisionChanged(string revision)
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (remoteRefreshQueued) return;
            remoteRefreshQueued = true;
            try { BeginInvoke(new Action(() => { remoteRefreshQueued = false; ApplyRemoteChange(revision); })); }
            catch (ObjectDisposedException) { remoteRefreshQueued = false; }
            catch (InvalidOperationException) { remoteRefreshQueued = false; }
        }

        // Hilo de la UI. Si no es buen momento (diálogo abierto, Configuración, edición) no se acepta la revisión:
        // el siguiente sondeo (5 s) la vuelve a detectar y reintenta. No muestra mensajes.
        private void ApplyRemoteChange(string revision)
        {
            if (IsDisposed || !IsHandleCreated || revisionPoller == null) return;
            if (!CanRefreshForRemoteChange()) return;
            try { UpdateCurrentView(true); }
            catch (Exception ex) { AppData.LogError(ex, "Actualización por cambios de otra caja"); }
            if (revisionPoller != null) revisionPoller.Accept(revision);
        }

        private bool CanRefreshForRemoteChange()
        {
            if (currentKey == null || currentKey == "settings") return false;   // Configuración no se pisa mientras se escribe
            // Reportes y Auditoría son consultas pesadas y la usuaria filtra a mano: no se relanzan solas en el hilo de la interfaz.
            if (currentKey == "reports" || currentKey == "audit") return false;
            if (!Enabled || WindowState == FormWindowState.Minimized) return false;   // MessageBox/diálogo modal deshabilita el formulario
            foreach (Form form in Application.OpenForms)
                if (form != this && form.Visible && form.Modal) return false;
            // Ventas conserva carrito y ticket en curso al refrescar (RenderCart repinta el carrito en memoria): se refresca aunque haya foco en un cuadro de texto.
            // Las demás vistas reconstruyen sus controles: no se refrescan mientras haya un editor con foco.
            if (currentKey != "sales" && IsEditorFocused()) return false;
            return true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private static bool IsEditorFocused()
        {
            Control focused;
            try { focused = Control.FromHandle(GetFocus()); }
            catch { return false; }
            if (focused == null) return false;
            if (focused is TextBoxBase || focused is NumericUpDown || focused is DateTimePicker) return true;
            var combo = focused as ComboBox;
            if (combo != null) return combo.DroppedDown || combo.DropDownStyle != ComboBoxStyle.DropDownList;
            var grid = focused as DataGridView;
            if (grid != null) return grid.IsCurrentCellInEditMode;
            return false;
        }

        private void UpdateCurrentView() { UpdateCurrentView(false); }

        private void UpdateCurrentView(bool automatic)
        {
            UpdateIdentity(automatic);
            Control control;
            if (currentKey != null && views.TryGetValue(currentKey, out control))
            {
                var refreshable = control as IRefreshable;
                if (refreshable != null) refreshable.RefreshView();
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2) Navigate("sales");
            else if (e.KeyCode == Keys.F3) Navigate("receipts");
            else if (e.KeyCode == Keys.F4) Navigate("shift");
            else if (e.KeyCode == Keys.F5) UpdateCurrentView();
        }

        // Elemento de navegación de la barra lateral: píldora SidebarActive + indicador verde de marca cuando está activo.
        private sealed class NavButton : Button
        {
            private readonly string glyph, label, hint;
            private bool pressed, active;
            private double indicator;   // 0..1: alto del indicador verde (crece desde el centro al activarse)
            private readonly HoverFade hoverFade;
            private static readonly Font IconFont = CreateIconFont();

            public NavButton(string glyph, string label, string hint)
            {
                this.glyph = glyph; this.label = label; this.hint = hint;
                hoverFade = new HoverFade(this);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                Height = 46;
                Margin = new Padding(0, 0, 0, Theme.Space.Xs);
                Cursor = Cursors.Hand;
                Font = Theme.Body;
                BackColor = Theme.SidebarBack;
                AccessibleName = label;
            }

            public bool Active
            {
                get { return active; }
                set
                {
                    if (active == value) return;
                    active = value;
                    if (active) Motion.Animate(this, "indicator", Motion.Normal, t => { indicator = t; Invalidate(); });
                    else { Motion.Cancel(this, "indicator"); indicator = 0; }
                    Invalidate();
                }
            }

            private static Font CreateIconFont()
            {
                using (var probe = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                        if (probe.Families.Any(f => f.Name == family)) return new Font(family, 13f, FontStyle.Regular, GraphicsUnit.Point);
                }
                return null;
            }

            // Button (ButtonBase) activa ControlStyles.Opaque: Windows Forms NUNCA llama a OnPaintBackground en WM_PAINT. Con
            // OptimizedDoubleBuffer el búfer es compartido por todos los controles, así que si OnPaint no pinta su fondo quedan
            // los píxeles del último control pintado (otro NavButton: textos encimados y fondo "activo" en todos).
            // Por eso el fondo completo se pinta aquí, al inicio de OnPaint.
            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.SidebarBack);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                var fill = active ? Theme.SidebarActive : Theme.Blend(Theme.SidebarBack, Theme.SidebarHover, hoverFade.Value);
                if (pressed && !active) fill = Theme.Blend(Theme.SidebarHover, Theme.SidebarActive, 0.6);
                if (fill != Theme.SidebarBack)
                    using (var path = Gfx.Round(rect, Theme.Radius.Control + 2))
                    using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);

                var barHeight = (int)Math.Round(22 * indicator);
                if (active && barHeight >= 2)
                    using (var path = Gfx.Round(new Rectangle(0, (Height - barHeight) / 2, 4, barHeight), 2))
                    using (var brush = new SolidBrush(Theme.Green)) g.FillPath(brush, path);

                var textColor = active ? Color.White : Theme.SidebarText;
                const int iconLeft = Theme.Space.Lg;
                var textLeft = iconLeft + 36;
                if (IconFont != null)
                    TextRenderer.DrawText(g, glyph, IconFont, new Rectangle(iconLeft, 0, 28, Height), active ? Color.White : Theme.SidebarMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                else textLeft = iconLeft;
                var hintWidth = string.IsNullOrEmpty(hint) ? 0 : 36;
                TextRenderer.DrawText(g, label, active ? Theme.Subhead : Theme.Body, new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - hintWidth - Theme.Space.Sm), Height), textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (hintWidth > 0)
                    TextRenderer.DrawText(g, hint, Theme.Caption, new Rectangle(Width - hintWidth - Theme.Space.Md, 0, hintWidth, Height), Theme.SidebarMuted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                if (Focused && ShowFocusCues)
                    using (var path = Gfx.Round(new Rectangle(1, 1, Width - 3, Height - 3), Theme.Radius.Control + 1))
                    using (var pen = new Pen(Theme.SidebarMuted, 1.5f)) g.DrawPath(pen, path);
            }

            protected override void OnMouseDown(MouseEventArgs mevent)
            {
                if (mevent.Button == MouseButtons.Left) { pressed = true; Invalidate(); Update(); }   // respuesta al presionar, no al soltar
                base.OnMouseDown(mevent);
            }
            protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }
            protected override void OnMouseEnter(EventArgs e) { hoverFade.Set(true); Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hoverFade.Set(false); if (!Capture) pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        }
    }
}
