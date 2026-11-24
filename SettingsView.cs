using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    // Configuración: una navegación de secciones a la izquierda y UNA sección a la vez a la derecha.
    // Secciones: Negocio · Usuarios · Promociones · Cargos por pérdida · Facturación electrónica (simulada) · Consulta de DNI · Copias de seguridad y datos · Réplica en Supabase.
    public sealed class SettingsView : UserControl, IRefreshable
    {
        private readonly TextBox store;
        private readonly TextBox employee;
        private readonly TextBox currency;
        private readonly Label dataPath;
        private readonly Label storageMode;
        private Button portableButton;
        private readonly CheckBox dniEnabled;
        private readonly TextBox dniToken;
        private readonly Label dniStatus;
        private readonly Label customersInfo;
        private readonly UserManagementPanel usersPanel;
        private readonly CheckBox promoSexta;
        private readonly NumericUpDown promoMinimo;
        private readonly NumericUpDown promoPorcentaje;
        // RF-22 / RN-06: cargos por pérdida y llaves fuera de uso.
        private readonly NumericUpDown lossKeyCharge;
        private readonly ComboBox lossLockers;
        private readonly Label lossLockersInfo;
        private Button lossRelease;
        private readonly CheckBox einvSimularRechazo;
        private readonly Label einvResumen;
        private readonly Pill pillAceptado;
        private readonly Pill pillPendiente;
        private readonly Pill pillRechazado;
        private readonly Pill pillAnulado;
        private Button einvReintentar;
        private bool refreshingEinv;
        private readonly CheckBox autoEnabled;
        private readonly TextBox autoFolder;
        private readonly NumericUpDown autoKeep;
        private readonly Label autoStatus;
        private Banner autoBanner;
        private Button autoNow;
        private StackCard autoCard;

        // Réplica en Supabase
        private TextBox sbHost, sbDatabase, sbUser, sbPassword, sbCa;
        private EdenField sbPasswordField, sbCaField;
        private NumericUpDown sbPort;
        private CheckBox sbEnabled;
        private Banner sbConnBanner, sbRunBanner, sbOtherPc, sbRestoreBanner, sbBlockBanner;
        private Button sbTest, sbSave, sbNow, sbRestore, sbForce;
        private int sbPendingDeletes;
        private Pill sbPill;
        private Label sbMessage, sbLast, sbRows, sbNext, sbError;
        private StackCard sbConnCard, sbStatusCard, sbRestoreCard;
        private bool sbDirty, sbRestoring, sbHasPassword;

        // RefreshView (que MainForm llama al cambiar los datos) no pisa lo que la dueña esté escribiendo:
        // cada grupo de campos se recarga solo si no tiene cambios sin guardar.
        private bool loading;
        private bool businessDirty, dniDirty, promoDirty, lossDirty;

        private readonly Dictionary<string, SectionPage> pages = new Dictionary<string, SectionPage>();
        private readonly Dictionary<string, NavItem> navItems = new Dictionary<string, NavItem>();
        private readonly List<string> navOrder = new List<string>();
        private string currentSection;

        public SettingsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Canvas;

            // ---- Controles de datos (se crean aquí para poder referenciarlos desde las tarjetas) ---------------------------
            store = new TextBox { MaxLength = 80 };
            employee = new TextBox { ReadOnly = true };
            currency = new TextBox { MaxLength = 5 };
            dniToken = new TextBox { UseSystemPasswordChar = true, MaxLength = 500 };
            promoMinimo = new NumericUpDown { Minimum = AppData.PromoMinPersonas, Maximum = AppData.PromoMaxPersonas, Value = 4 };
            promoPorcentaje = new NumericUpDown { DecimalPlaces = 2, Minimum = 0m, Maximum = AppData.PromoMaxPorcentaje, Value = 10m };
            dniEnabled = MakeCheck("Activar la consulta automática al completar 8 dígitos");
            promoSexta = MakeCheck("Sexta visita gratis (requiere DNI del cliente)");
            einvSimularRechazo = MakeCheck("Simular rechazo (para probar el reintento)");
            einvSimularRechazo.CheckedChanged += EinvSimularRechazo_Changed;
            store.TextChanged += (s, e) => { if (!loading) businessDirty = true; };
            currency.TextChanged += (s, e) => { if (!loading) businessDirty = true; };
            dniEnabled.CheckedChanged += (s, e) => { if (!loading) dniDirty = true; };
            dniToken.TextChanged += (s, e) => { if (!loading) dniDirty = true; };
            promoSexta.CheckedChanged += (s, e) => { if (!loading) promoDirty = true; };
            promoMinimo.ValueChanged += (s, e) => { if (!loading) promoDirty = true; };
            promoPorcentaje.ValueChanged += (s, e) => { if (!loading) promoDirty = true; };
            lossKeyCharge = new NumericUpDown { DecimalPlaces = 2, Minimum = 0m, Maximum = AppData.CargoPerdidaMaximo, ThousandsSeparator = true };
            lossKeyCharge.ValueChanged += (s, e) => { if (!loading) lossDirty = true; };
            lossLockers = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.Body };
            lossLockersInfo = new Label { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false };

            autoEnabled = MakeCheck("Copia automática");
            autoFolder = new TextBox { MaxLength = 260 };
            autoKeep = new NumericUpDown { Minimum = 1, Maximum = AppData.AutoBackupMaxKeep, Value = AppData.AutoBackupDefaultKeep };
            autoStatus = new Label { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false };
            autoEnabled.CheckedChanged += (s, e) => { if (!loading) ApplyAutoBackupConfig(true); };
            autoFolder.Leave += (s, e) => { if (!loading) ApplyAutoBackupConfig(false); };
            autoKeep.ValueChanged += (s, e) => { if (!loading) ApplyAutoBackupConfig(false); };

            dataPath = new Label { Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = false };
            storageMode = new Label { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false };
            dniStatus = new Label { Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = false };
            customersInfo = new Label { Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = false };
            einvResumen = new Label { Font = Theme.Subhead, ForeColor = Theme.Ink, AutoSize = false };
            pillAceptado = Theme.Pill("Aceptados: 0", Tone.Success, true);
            pillPendiente = Theme.Pill("Pendientes: 0", Tone.Warning, true);
            pillRechazado = Theme.Pill("Rechazados: 0", Tone.Danger, true);
            pillAnulado = Theme.Pill("Anulados: 0", Tone.Neutral, true);

            // ---- Estructura: cabecera + [navegación | sección] ---------------------------------------------------------------
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
            var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
            var navDivider = Theme.Separator(true); navDivider.Dock = DockStyle.Left;
            var nav = new Panel { Dock = DockStyle.Left, Width = 324, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Xl, Theme.Space.Lg, Theme.Space.Xl) };
            body.Controls.Add(pageHost);
            body.Controls.Add(navDivider);
            body.Controls.Add(nav);
            Controls.Add(body);
            var header = new ScreenHeader { Title = "Configuración", Subtitle = "Datos del negocio, usuarios, promociones y copias de seguridad" };
            Controls.Add(header);

            // ---- Secciones -------------------------------------------------------------------------------------------------
            usersPanel = new UserManagementPanel();
            AddSection(pageHost, "negocio", "Negocio", BuildBusiness());
            AddSection(pageHost, "usuarios", "Usuarios", new[] { usersPanel }, true);
            AddSection(pageHost, "promociones", "Promociones", BuildPromotions());
            AddSection(pageHost, "perdidas", "Cargos por pérdida", BuildLossCharges());
            AddSection(pageHost, "facturacion", "Facturación electrónica (simulada)", BuildInvoicing());
            AddSection(pageHost, "dni", "Consulta de DNI", BuildDni());
            AddSection(pageHost, "copias", "Copias de seguridad y datos", BuildBackups());
            AddSection(pageHost, "supabase", "Réplica en Supabase", BuildSupabase());

            // Los ítems Dock=Top se apilan al revés: se añaden del último al primero.
            for (var i = navOrder.Count - 1; i >= 0; i--) nav.Controls.Add(navItems[navOrder[i]]);

            ShowSection("negocio");
            RefreshView(true);
            AppData.ComprobantesChanged += OnComprobantesChanged;
            AppData.SupabaseReplicaStatusChanged += OnSupabaseStatusChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                AppData.ComprobantesChanged -= OnComprobantesChanged;
                AppData.SupabaseReplicaStatusChanged -= OnSupabaseStatusChanged;
            }
            base.Dispose(disposing);
        }

        // ================================================================================================================
        // Navegación
        // ================================================================================================================
        private void AddSection(Control host, string key, string title, IEnumerable<StackCard> cards)
        {
            AddSection(host, key, title, cards.Cast<CardPanel>(), false);
        }

        private void AddSection(Control host, string key, string title, IEnumerable<CardPanel> cards, bool fillHeight)
        {
            var page = new SectionPage { Dock = DockStyle.Fill, Visible = false, FillHeight = fillHeight };
            foreach (var card in cards) page.AddCard(card);
            host.Controls.Add(page);
            pages[key] = page;
            var item = new NavItem { Text = title, Dock = DockStyle.Top, Height = 44, Tag = key };
            item.Activated += (s, e) => ShowSection((string)((NavItem)s).Tag);
            item.StepRequested += (s, direction) =>
            {
                var index = navOrder.IndexOf(currentSection) + direction;
                if (index < 0 || index >= navOrder.Count) return;
                ShowSection(navOrder[index]);
                navItems[currentSection].Focus();
            };
            navItems[key] = item;
            navOrder.Add(key);
        }

        // Muestra una sola sección. También la usa la herramienta de capturas (Capturas.ps1) por reflexión.
        private void ShowSection(string key)
        {
            if (!pages.ContainsKey(key)) return;
            currentSection = key;
            foreach (var pair in pages) pair.Value.Visible = pair.Key == key;
            foreach (var pair in navItems) pair.Value.Selected = pair.Key == key;
            pages[key].Relayout();
        }

        // ================================================================================================================
        // Tarjetas por sección
        // ================================================================================================================
        private IEnumerable<StackCard> BuildBusiness()
        {
            var card = new StackCard();
            card.Add(CardTitle("Datos del negocio"), 0);
            card.Add(Note("Estos datos aparecen en la barra lateral y en los recibos."), Theme.Space.Xs);

            card.Add(Theme.LabelField("Nombre del negocio"), Theme.Space.Xl);
            card.Add(Theme.Field(store), Theme.Space.Xs);
            card.Add(Theme.LabelField("Usuario con sesión abierta"), Theme.Space.Lg);
            var employeeField = Theme.Field(employee);
            card.Add(employeeField, Theme.Space.Xs, 440);
            card.Add(Theme.LabelField("Símbolo de moneda"), Theme.Space.Lg);
            card.Add(Theme.Field(currency), Theme.Space.Xs, 160);

            var save = Theme.Button("Guardar cambios", ButtonVariant.Primary);
            save.Click += Save_Click;
            card.Add(ButtonRow(save), Theme.Space.Xl);
            yield return card;
        }

        private IEnumerable<StackCard> BuildPromotions()
        {
            var card = new StackCard();
            card.Add(CardTitle("Promociones"), 0);
            card.Add(Note("No se acumulan: si un cobro califica para ambas, se aplica la de mayor descuento."), Theme.Space.Xs);

            card.Add(Theme.Separator(), Theme.Space.Lg);
            card.Add(promoSexta, Theme.Space.Lg);
            card.Add(Note("Cuando el cliente completa su sexta visita (por DNI), la línea de categoría SAUNA queda exonerada."), Theme.Space.Xs, 0, Theme.Caption);

            card.Add(Theme.Separator(), Theme.Space.Lg);
            card.Add(Theme.Label("Descuento grupal", 11f, FontStyle.Bold), Theme.Space.Lg);
            card.Add(Theme.LabelField("Mínimo de personas (" + AppData.PromoMinPersonas + " a " + AppData.PromoMaxPersonas + ")"), Theme.Space.Md);
            card.Add(Theme.Field(promoMinimo), Theme.Space.Xs, 160);
            card.Add(Theme.LabelField("Porcentaje de descuento (0 a " + AppData.PromoMaxPorcentaje.ToString("0") + ")"), Theme.Space.Lg);
            card.Add(Theme.Field(promoPorcentaje), Theme.Space.Xs, 160);

            var savePromo = Theme.Button("Guardar promociones", ButtonVariant.Primary);
            savePromo.Click += SavePromotions_Click;
            card.Add(ButtonRow(savePromo), Theme.Space.Xl);
            yield return card;
        }

        // RF-22: la dueña configura el cargo por llave no devuelta; el de cada implemento se edita en Inventario (Editar artículo).
        // RN-06: las llaves no devueltas quedan fuera de uso hasta que la dueña confirma aquí que se repusieron.
        private IEnumerable<StackCard> BuildLossCharges()
        {
            var card = new StackCard();
            card.Add(CardTitle("Cargos por pérdida"), 0);
            card.Add(Note("Si al finalizar la visita el cliente no devuelve una llave o un implemento, este cargo se suma a su cuenta."), Theme.Space.Xs);
            card.Add(Theme.LabelField("Cargo por pérdida de llave (S/, 0 a " + AppData.CargoPerdidaMaximo.ToString("0") + ")"), Theme.Space.Lg);
            card.Add(Theme.Field(lossKeyCharge), Theme.Space.Xs, 160);
            card.Add(Note("Con 0 no se cobra nada, pero la pérdida igual queda registrada en el recibo y en Auditoría.", Theme.Caption), Theme.Space.Xs);
            card.Add(Note("El cargo de cada implemento (toallas, sandalias y demás alquileres) se configura en Inventario, al editar el artículo.", Theme.Caption), Theme.Space.Sm);
            var save = Theme.Button("Guardar cargo", ButtonVariant.Primary);
            save.Click += SaveLossCharge_Click;
            card.Add(ButtonRow(save), Theme.Space.Xl);
            yield return card;

            var lockers = new StackCard();
            lockers.Add(CardTitle("Llaves fuera de uso"), 0);
            lockers.Add(Note("Una llave no devuelta no se asigna a otra visita hasta que usted confirme que la recuperó o la repuso."), Theme.Space.Xs);
            lockers.Add(lossLockersInfo, Theme.Space.Lg);
            lockers.Add(Theme.Field(lossLockers), Theme.Space.Sm, 160);
            lossRelease = Theme.Button("Marcar como repuesta", ButtonVariant.Secondary);
            lossRelease.Click += ReleaseLocker_Click;
            lockers.Add(ButtonRow(lossRelease), Theme.Space.Lg);
            yield return lockers;
        }

        private void RefreshLossCharges()
        {
            var wasLoading = loading;
            loading = true;   // el valor leído de la base no cuenta como cambio sin guardar
            try { if (!lossDirty) lossKeyCharge.Value = Math.Min(lossKeyCharge.Maximum, Math.Max(lossKeyCharge.Minimum, AppData.CargoPerdidaLlave)); }
            finally { loading = wasLoading; }
            var list = AppData.LockersOutOfService;
            var previous = lossLockers.SelectedItem as string;
            lossLockers.Items.Clear();
            foreach (var locker in list) lossLockers.Items.Add("Llave N.º " + locker);
            if (previous != null && lossLockers.Items.Contains(previous)) lossLockers.SelectedItem = previous;
            else if (lossLockers.Items.Count > 0) lossLockers.SelectedIndex = 0;
            lossLockersInfo.Text = list.Count == 0 ? "No hay llaves fuera de uso." : list.Count == 1 ? "1 llave fuera de uso." : list.Count + " llaves fuera de uso.";
            lossLockersInfo.ForeColor = list.Count == 0 ? Theme.Muted : Theme.Warning;
            lossLockers.Enabled = lossRelease.Enabled = list.Count > 0;
        }

        private void SaveLossCharge_Click(object sender, EventArgs e)
        {
            try
            {
                AppData.SetCargoPerdidaLlave(Math.Round(lossKeyCharge.Value, 2));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Msg(Tone.Warning, ex.Message, "Cargos por pérdida");
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Guardar cargo por pérdida (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Cargos por pérdida");
                return;
            }
            lossDirty = false;
            Msg(Tone.Success, "Cargo por pérdida de llave guardado.", "Edén POS");
        }

        private void ReleaseLocker_Click(object sender, EventArgs e)
        {
            var text = lossLockers.SelectedItem as string;
            int locker;
            if (text == null || !int.TryParse(text.Substring(text.LastIndexOf(' ') + 1), out locker)) return;
            if (MessageBox.Show(FindForm(), "¿Confirma que la llave N.º " + locker + " se recuperó o se repuso? Volverá a estar disponible para las visitas.",
                    "Liberar llave", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                AppData.ReleaseLockerOutOfService(locker);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Msg(Tone.Warning, ex.Message, "Llaves fuera de uso");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Liberar llave fuera de uso (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Llaves fuera de uso");
            }
            try { RefreshLossCharges(); }
            catch (MySqlConnector.MySqlException ex) { AppData.LogError(ex, "Refrescar llaves fuera de uso"); }
        }

        private IEnumerable<StackCard> BuildInvoicing()
        {
            var status = new StackCard();
            status.Add(CardTitle("Facturación electrónica (simulada)"), 0);
            status.Add(Theme.Banner("Simulado: no se envía nada a SUNAT ni a ningún OSE/PSE, y no hay credenciales reales. Cada venta genera una boleta (serie B001) que el emisor simulado acepta al instante.", Tone.Info), Theme.Space.Lg);
            status.Add(Theme.LabelField("Comprobantes por estado"), Theme.Space.Xl);
            status.Add(einvResumen, Theme.Space.Xs);
            var pills = new FlowLayoutPanel { WrapContents = true, BackColor = Theme.Surface, Margin = Padding.Empty };
            foreach (var pill in new[] { pillAceptado, pillPendiente, pillRechazado, pillAnulado }) { pill.Margin = new Padding(0, 0, Theme.Space.Sm, Theme.Space.Sm); pills.Controls.Add(pill); }
            status.Add(pills, Theme.Space.Sm);
            status.Add(Note("Anulados: comprobantes de recibos reembolsados."), 0, 0, Theme.Caption);
            yield return status;

            var tools = new StackCard();
            tools.Add(CardTitle("Probar y reintentar"), 0);
            tools.Add(einvSimularRechazo, Theme.Space.Lg);
            tools.Add(Note("Con esta opción, el emisor simulado rechaza los comprobantes nuevos; sirve para probar el reintento."), Theme.Space.Xs, 0, Theme.Caption);
            tools.Add(Note("Los rechazados y pendientes se reintentan solos cada " + (int)ElectronicInvoicingService.IntervaloPorDefecto.TotalMinutes + " minutos mientras la app esté abierta (máximo " + ElectronicInvoicingService.MaxIntentos + " intentos automáticos)."), Theme.Space.Lg);
            einvReintentar = Theme.Button("Reintentar envíos", ButtonVariant.Primary);
            einvReintentar.Click += EinvReintentar_Click;
            tools.Add(ButtonRow(einvReintentar), Theme.Space.Xl);
            yield return tools;
        }

        private IEnumerable<StackCard> BuildDni()
        {
            var card = new StackCard();
            card.Add(CardTitle("Consulta de clientes por DNI"), 0);
            card.Add(Note("Proveedor opcional: ApiPeruDev, basado en el padrón reducido de SUNAT y otras fuentes públicas."), Theme.Space.Xs);
            card.Add(Theme.Separator(), Theme.Space.Lg);
            card.Add(dniEnabled, Theme.Space.Lg);

            card.Add(Theme.LabelField("Token privado de ApiPeruDev"), Theme.Space.Lg);
            var row = new Panel { Height = Theme.Dim.Field, BackColor = Theme.Surface };
            var tokenField = Theme.Field(dniToken);
            tokenField.Dock = DockStyle.Fill;
            row.Controls.Add(tokenField);
            var showToken = Theme.Button("Mostrar", ButtonVariant.Secondary);
            showToken.Dock = DockStyle.Right; showToken.Width = 112; showToken.Height = Theme.Dim.Field; showToken.Margin = Padding.Empty;
            showToken.Click += (s, e) =>
            {
                dniToken.UseSystemPasswordChar = !dniToken.UseSystemPasswordChar;
                showToken.Text = dniToken.UseSystemPasswordChar ? "Mostrar" : "Ocultar";
            };
            row.Controls.Add(showToken);
            card.Add(row, Theme.Space.Xs);
            card.Add(dniStatus, Theme.Space.Sm);
            card.Add(Note("El token se cifra para el usuario actual de Windows. El nombre obtenido debe verificarse con el DNI físico del cliente."), Theme.Space.Md, 0, Theme.Caption);

            var saveDni = Theme.Button("Guardar configuración", ButtonVariant.Primary);
            saveDni.Click += Save_Click;
            card.Add(ButtonRow(saveDni), Theme.Space.Xl);
            yield return card;
        }

        private IEnumerable<StackCard> BuildBackups()
        {
            // Exportar (acción principal de la sección)
            var export = new StackCard();
            export.Add(CardTitle("Exportar una copia"), 0);
            export.Add(Note("Guarda productos, recibos, turnos, usuarios y configuración en un archivo .edenbackup. Por seguridad, el token DNI y la cadena de conexión no se incluyen."), Theme.Space.Xs);
            var exportButton = Theme.Button("Exportar copia", ButtonVariant.Primary);
            exportButton.Click += ExportBackup_Click;
            export.Add(ButtonRow(exportButton), Theme.Space.Lg);
            yield return export;

            // Copia automática a una carpeta (OneDrive / Google Drive)
            autoCard = new StackCard();
            autoCard.Add(CardTitle("Copia automática"), 0);
            autoCard.Add(Note("Elija una carpeta de OneDrive o Google Drive para que la copia también quede en la nube. Para recuperar los datos en otra PC use Importar copia."), Theme.Space.Xs);
            autoCard.Add(autoEnabled, Theme.Space.Lg);
            autoCard.Add(Note("Se guarda una copia al cerrar cada turno y, como mínimo, una vez al día mientras Edén POS esté abierto.", Theme.Caption), Theme.Space.Xs);
            autoCard.Add(Theme.LabelField("Carpeta de destino"), Theme.Space.Lg);
            var folderRow = new Panel { Height = Theme.Dim.Field, BackColor = Theme.Surface };
            var folderField = Theme.Field(autoFolder);
            folderField.Dock = DockStyle.Fill;
            folderRow.Controls.Add(folderField);
            var folderGap = new Panel { Dock = DockStyle.Right, Width = Theme.Space.Sm, BackColor = Theme.Surface };
            folderRow.Controls.Add(folderGap);
            var chooseFolder = Theme.Button("Elegir carpeta…", ButtonVariant.Secondary);
            chooseFolder.Dock = DockStyle.Right; chooseFolder.Width = 168; chooseFolder.Height = Theme.Dim.Field; chooseFolder.Margin = Padding.Empty;
            chooseFolder.Click += ChooseAutoBackupFolder_Click;
            folderRow.Controls.Add(chooseFolder);
            autoCard.Add(folderRow, Theme.Space.Xs);
            autoCard.Add(Theme.LabelField("Copias que se conservan"), Theme.Space.Lg);
            autoCard.Add(Theme.Field(autoKeep), Theme.Space.Xs, 160);
            autoCard.Add(Note("Se borran las más antiguas de este equipo. Los archivos de otros equipos y cualquier otro archivo de la carpeta no se tocan.", Theme.Caption), Theme.Space.Xs);
            autoCard.Add(autoStatus, Theme.Space.Lg);
            autoBanner = Theme.Banner("", Tone.Info);
            autoBanner.Visible = false;
            autoCard.Add(autoBanner, Theme.Space.Sm);
            autoNow = Theme.Button("Hacer copia ahora", ButtonVariant.Secondary);
            autoNow.Click += AutoBackupNow_Click;
            autoCard.Add(ButtonRow(autoNow), Theme.Space.Lg);
            yield return autoCard;

            // Importar: acción con advertencia clara
            var import = new StackCard();
            import.Add(CardTitle("Importar una copia"), 0);
            import.Add(Theme.Banner("Importar reemplaza todos los datos actuales (productos, recibos, turnos, usuarios y configuración) por los de la copia. Antes de continuar, exporte una copia de lo que tiene ahora.", Tone.Warning), Theme.Space.Lg);
            var importButton = Theme.Button("Importar copia…", ButtonVariant.Secondary);
            importButton.Click += ImportBackup_Click;
            import.Add(ButtonRow(importButton), Theme.Space.Lg);
            yield return import;

            // Almacenamiento
            var storage = new StackCard();
            storage.Add(CardTitle("Almacenamiento"), 0);
            storage.Add(Theme.LabelField("Carpeta de datos locales"), Theme.Space.Lg);
            storage.Add(dataPath, Theme.Space.Xs);
            storage.Add(storageMode, Theme.Space.Md);
            portableButton = Theme.Button("Activar modo portable", ButtonVariant.Secondary);
            portableButton.Click += TogglePortable_Click;
            var open = Theme.Button("Abrir carpeta", ButtonVariant.Secondary);
            open.Click += (s, e) => AppData.OpenDataFolder();
            storage.Add(ButtonRow(portableButton, open), Theme.Space.Lg);
            yield return storage;

            // Zona de peligro
            var danger = new StackCard { BorderColor = Theme.Danger };
            var dangerTitle = CardTitle("Zona de peligro");
            dangerTitle.ForeColor = Theme.Danger;
            danger.Add(dangerTitle, 0);
            danger.Add(Note("Borra los DNI y nombres de clientes guardados en este equipo. Los recibos existentes no se modifican. Esta acción no se puede deshacer."), Theme.Space.Xs);
            danger.Add(customersInfo, Theme.Space.Md);
            var clearCustomers = Theme.Button("Borrar clientes locales", ButtonVariant.Destructive);
            clearCustomers.Click += (s, e) =>
            {
                if (AppData.Customers.Count == 0) { Msg(Tone.Info, "No hay clientes guardados localmente.", "Clientes"); return; }
                if (MessageBox.Show(FindForm(), "Se eliminarán los DNI y nombres guardados localmente. Los recibos existentes no se modificarán. ¿Continuar?", "Privacidad de clientes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                AppData.ClearCustomers();
                RefreshView();
            };
            danger.Add(ButtonRow(clearCustomers), Theme.Space.Lg);
            yield return danger;
        }

        private IEnumerable<StackCard> BuildSupabase()
        {
            sbHost = new TextBox { MaxLength = 200 };
            sbPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 5432 };
            sbDatabase = new TextBox { MaxLength = 63, Text = "postgres" };
            sbUser = new TextBox { MaxLength = 100 };
            sbPassword = new TextBox { UseSystemPasswordChar = true, MaxLength = 200 };
            sbCa = new TextBox { MaxLength = 260 };
            sbEnabled = MakeCheck("Activar réplica en esta PC");
            foreach (var box in new[] { sbHost, sbDatabase, sbUser, sbPassword, sbCa }) box.TextChanged += (s, e) => { if (!loading) sbDirty = true; };
            sbPort.ValueChanged += (s, e) => { if (!loading) sbDirty = true; };
            sbEnabled.CheckedChanged += (s, e) => { if (!loading) sbDirty = true; };

            // a) Conexión
            sbConnCard = new StackCard();
            sbConnCard.Add(CardTitle("Conexión"), 0);
            sbConnCard.Add(Note("Guarda una copia de sus datos en su proyecto de Supabase cada pocos minutos. EdenPOS sigue funcionando sin internet; los cambios se suben cuando vuelva la conexión. Pasos en devdb\\supabase\\LEEME.md."), Theme.Space.Xs);
            sbConnCard.Add(Theme.LabelField("Host (Session pooler)"), Theme.Space.Xl);
            sbConnCard.Add(Theme.Field(sbHost), Theme.Space.Xs);
            sbConnCard.Add(Theme.LabelField("Puerto"), Theme.Space.Lg);
            sbConnCard.Add(Theme.Field(sbPort), Theme.Space.Xs, 160);
            sbConnCard.Add(Theme.LabelField("Base de datos"), Theme.Space.Lg);
            sbConnCard.Add(Theme.Field(sbDatabase), Theme.Space.Xs, 320);
            sbConnCard.Add(Theme.LabelField("Usuario (por ejemplo, edenpos_replica.xxxx)"), Theme.Space.Lg);
            sbConnCard.Add(Theme.Field(sbUser), Theme.Space.Xs);
            sbConnCard.Add(Theme.LabelField("Contraseña"), Theme.Space.Lg);
            sbPasswordField = Theme.Field(sbPassword);
            sbConnCard.Add(sbPasswordField, Theme.Space.Xs);
            sbConnCard.Add(Theme.LabelField("Certificado CA"), Theme.Space.Lg);
            var caRow = new Panel { Height = Theme.Dim.Field, BackColor = Theme.Surface };
            sbCaField = Theme.Field(sbCa);
            sbCaField.Dock = DockStyle.Fill;
            caRow.Controls.Add(sbCaField);
            caRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Theme.Space.Sm, BackColor = Theme.Surface });
            var chooseCa = Theme.Button("Elegir…", ButtonVariant.Secondary);
            chooseCa.Dock = DockStyle.Right; chooseCa.Width = 120; chooseCa.Height = Theme.Dim.Field; chooseCa.Margin = Padding.Empty;
            chooseCa.Click += SupabaseChooseCa_Click;
            caRow.Controls.Add(chooseCa);
            sbConnCard.Add(caRow, Theme.Space.Xs);
            sbConnCard.Add(Theme.Separator(), Theme.Space.Lg);
            sbConnCard.Add(sbEnabled, Theme.Space.Lg);
            sbConnCard.Add(Note("Actívela solo en la PC que tiene la base principal.", Theme.Caption), Theme.Space.Xs);
            sbConnBanner = Theme.Banner("", Tone.Info);
            sbConnBanner.Visible = false;
            sbConnCard.Add(sbConnBanner, Theme.Space.Lg);
            sbTest = Theme.Button("Probar conexión", ButtonVariant.Secondary);
            sbTest.Click += SupabaseTest_Click;
            sbSave = Theme.Button("Guardar", ButtonVariant.Primary);
            sbSave.Click += SupabaseSave_Click;
            sbConnCard.Add(ButtonRow(sbTest, sbSave), Theme.Space.Lg);
            yield return sbConnCard;

            // b) Estado
            sbStatusCard = new StackCard();
            sbStatusCard.Add(CardTitle("Estado"), 0);
            sbPill = Theme.Pill("Desactivada", Tone.Neutral, true);
            var pillRow = new FlowLayoutPanel { WrapContents = true, BackColor = Theme.Surface, Margin = Padding.Empty };
            sbPill.Margin = new Padding(0, 0, Theme.Space.Sm, Theme.Space.Sm);
            pillRow.Controls.Add(sbPill);
            sbStatusCard.Add(pillRow, Theme.Space.Lg);
            sbMessage = new Label { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false };
            sbLast = new Label { Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = false };
            sbRows = new Label { Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = false };
            sbNext = new Label { Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = false };
            sbError = new Label { Font = Theme.Body, ForeColor = Theme.Danger, AutoSize = false, Visible = false };
            sbStatusCard.Add(sbMessage, 0);
            sbStatusCard.Add(sbLast, Theme.Space.Md);
            sbStatusCard.Add(sbRows, Theme.Space.Xs);
            sbStatusCard.Add(sbNext, Theme.Space.Xs);
            sbStatusCard.Add(sbError, Theme.Space.Xs);
            sbOtherPc = Theme.Banner("", Tone.Warning);
            sbOtherPc.Visible = false;
            sbStatusCard.Add(sbOtherPc, Theme.Space.Lg);
            sbRunBanner = Theme.Banner("", Tone.Info);
            sbRunBanner.Visible = false;
            sbStatusCard.Add(sbRunBanner, Theme.Space.Lg);
            sbBlockBanner = Theme.Banner("", Tone.Warning);
            sbBlockBanner.Visible = false;
            sbStatusCard.Add(sbBlockBanner, Theme.Space.Lg);
            sbNow = Theme.Button("Subir ahora", ButtonVariant.Secondary);
            sbNow.Click += SupabaseNow_Click;
            sbForce = Theme.Button("Subir de todos modos…", ButtonVariant.Destructive);
            sbForce.Click += SupabaseForce_Click;
            sbForce.Visible = false;
            sbStatusCard.Add(ButtonRow(sbNow, sbForce), Theme.Space.Lg);
            sbStatusCard.Add(Note("La réplica incluye los usuarios con sus PIN cifrados (no los PIN en claro), necesarios para restaurar. El token de DNI no se sube.", Theme.Caption), Theme.Space.Xs);
            yield return sbStatusCard;

            // c) Restaurar
            sbRestoreCard = new StackCard();
            sbRestoreCard.Add(CardTitle("Restaurar desde Supabase"), 0);
            sbRestoreCard.Add(Theme.Banner("Reemplaza todos los datos de esta PC por los de Supabase. Antes se guarda una copia local automática.", Tone.Warning), Theme.Space.Lg);
            sbRestoreBanner = Theme.Banner("", Tone.Info);
            sbRestoreBanner.Visible = false;
            sbRestoreCard.Add(sbRestoreBanner, Theme.Space.Md);
            sbRestore = Theme.Button("Restaurar desde Supabase…", ButtonVariant.Destructive);
            sbRestore.Click += SupabaseRestore_Click;
            sbRestoreCard.Add(ButtonRow(sbRestore), Theme.Space.Lg);
            yield return sbRestoreCard;
        }

        // ---- Helpers de construcción ------------------------------------------------------------------------------------
        private static Label CardTitle(string text)
        {
            return new Label { Text = text, Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = false };
        }

        private static Label Note(string text) { return Note(text, Theme.Body); }

        private static Label Note(string text, Font font)
        {
            return new Label { Text = text, Font = font, ForeColor = Theme.Muted, AutoSize = false };
        }

        private static CheckBox MakeCheck(string text)
        {
            return new CheckBox { Text = text, Font = Theme.Subhead, ForeColor = Theme.Ink, BackColor = Theme.Surface, AutoSize = false, Height = 30, Cursor = Cursors.Hand };
        }

        private static FlowLayoutPanel ButtonRow(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { WrapContents = true, BackColor = Theme.Surface, Margin = Padding.Empty };
            foreach (var button in buttons)
            {
                var text = TextRenderer.MeasureText(button.Text, button.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                button.Width = Math.Max(140, text.Width + Theme.Space.Xl * 2);
                button.Margin = new Padding(0, 0, Theme.Space.Sm, Theme.Space.Sm);
                row.Controls.Add(button);
            }
            return row;
        }

        private void Msg(Tone tone, string text, string title)
        {
            Theme.Message(FindForm(), tone, text, title);
        }

        // ================================================================================================================
        // Datos
        // ================================================================================================================
        // Los envíos de comprobantes solo refrescan el resumen de facturación: no tocan los campos que la dueña esté escribiendo.
        // Puede llegar desde un hilo de fondo: se pasa al hilo de la interfaz.
        private void OnComprobantesChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) RefreshEinv(); })); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        public void RefreshView()
        {
            RefreshView(false);
        }

        // force = true descarta los cambios sin guardar (primera carga, tras importar una copia).
        private void RefreshView(bool force)
        {
            if (force) businessDirty = dniDirty = promoDirty = false;
            AppSettings settings;
            try
            {
                settings = AppData.Settings;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Configuración (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Configuración");
                return;
            }
            loading = true;
            try
            {
                employee.Text = AppSession.UserName + " — " + AppSession.RoleName;
                if (!businessDirty)
                {
                    store.Text = settings.StoreName;
                    currency.Text = settings.CurrencySymbol;
                }
                dataPath.Text = AppData.DataDirectory;
                portableButton.Text = AppData.IsPortableMode ? "Desactivar modo portable" : "Activar modo portable";
                storageMode.Text = AppData.IsPortableMode
                    ? "Modo portable activo: datos e imágenes viajan junto al ejecutable. El token DNI debe configurarse de nuevo en otra cuenta de Windows."
                    : "Modo local: datos protegidos en el perfil de Windows. Use Exportar e Importar para trasladarlos a otra computadora.";
                if (!promoDirty)
                {
                    promoSexta.Checked = settings.PromoSextaVisitaActiva;
                    promoMinimo.Value = Math.Min(promoMinimo.Maximum, Math.Max(promoMinimo.Minimum, settings.PromoGrupalMinimoPersonas));
                    promoPorcentaje.Value = Math.Min(promoPorcentaje.Maximum, Math.Max(promoPorcentaje.Minimum, settings.PromoGrupalPorcentaje));
                }
                usersPanel.RefreshUsers();
                RefreshLossCharges();
                RefreshAutoBackup();
                RefreshEinv();
                RefreshSupabase(force);
                var customers = AppData.Customers.Count;
                customersInfo.Text = customers == 1 ? "Hay 1 cliente guardado localmente." : "Hay " + customers + " clientes guardados localmente.";
                var environmentToken = Environment.GetEnvironmentVariable("EDENPOS_DNI_TOKEN");
                if (!string.IsNullOrWhiteSpace(environmentToken))
                {
                    dniToken.Text = "";
                    dniToken.ReadOnly = true;
                    if (!dniDirty) dniEnabled.Checked = settings.DniLookupEnabled;
                    dniStatus.Text = "Token suministrado mediante EDENPOS_DNI_TOKEN • " + customers + " clientes locales";
                }
                else
                {
                    dniToken.ReadOnly = false;
                    if (!dniDirty)
                    {
                        dniEnabled.Checked = settings.DniLookupEnabled;
                        dniToken.Text = AppData.UnprotectSecret(settings.DniApiTokenProtected);
                    }
                    dniStatus.Text = (dniToken.TextLength > 0 ? "Token configurado" : "Token pendiente") + " • " + customers + " clientes locales";
                }
            }
            finally { loading = false; }
        }

        private void RefreshEinv()
        {
            refreshingEinv = true;
            try
            {
                einvSimularRechazo.Checked = AppData.SimularRechazoComprobantes;
                var r = AppData.GetComprobantesResumen();
                einvResumen.Text = "Comprobantes simulados: " + r.Total;
                einvResumen.ForeColor = Theme.Ink;
                pillAceptado.Text = "Aceptados: " + r.Aceptado;
                pillPendiente.Text = "Pendientes: " + r.Pendiente;
                pillRechazado.Text = "Rechazados: " + r.Rechazado;
                pillAnulado.Text = "Anulados: " + r.Anulado;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Resumen de comprobantes (base de datos)");
                einvResumen.Text = "No se pudo leer el resumen de comprobantes.";
                einvResumen.ForeColor = Theme.Danger;
            }
            finally { refreshingEinv = false; }
        }

        private void EinvSimularRechazo_Changed(object sender, EventArgs e)
        {
            if (refreshingEinv) return;
            try { AppData.SimularRechazoComprobantes = einvSimularRechazo.Checked; }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Guardar simulación de rechazo");
                Msg(Tone.Warning, "No se pudo guardar la opción en la base de datos.", "Facturación electrónica (simulada)");
            }
        }

        private async void EinvReintentar_Click(object sender, EventArgs e)
        {
            einvReintentar.Enabled = false;
            try
            {
                var r = await System.Threading.Tasks.Task.Run(() => AppData.ReintentarComprobantes(true));
                var text = r.YaEnCurso ? "Ya hay un reintento en curso. Inténtelo de nuevo en unos segundos."
                    : r.Procesados == 0 ? "No había comprobantes pendientes ni rechazados."
                    : "Reintento simulado terminado: " + r.Aceptados + " aceptados, " + r.Rechazados + " rechazados.";
                Msg(Tone.Info, text, "Facturación electrónica (simulada)");
            }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Reintentar envíos de comprobantes");
                Msg(Tone.Warning, ex is MySqlConnector.MySqlException ? AppData.DescribeDatabaseError((MySqlConnector.MySqlException)ex) : "No se pudieron reintentar los envíos. El detalle quedó en errors.log.", "Facturación electrónica (simulada)");
            }
            finally
            {
                einvReintentar.Enabled = true;
                RefreshEinv();
            }
        }

        // ---- Copia automática ---------------------------------------------------------------------------------------------
        private void ShowAutoBanner(Tone tone, string text)
        {
            autoBanner.Tone = tone;
            autoBanner.Text = text;
            autoBanner.Visible = !string.IsNullOrEmpty(text);
            autoCard.Relayout();
            SectionPage page;
            if (pages.TryGetValue("copias", out page)) page.Relayout();
        }

        // Muestra en pantalla lo guardado en esta PC. Mientras la dueña escribe la ruta no se pisa el campo.
        private void RefreshAutoBackup()
        {
            var config = AppData.GetAutoBackupConfig();
            if (!autoFolder.Focused) autoFolder.Text = config.Folder;
            autoEnabled.Checked = config.Enabled;
            autoKeep.Value = Math.Min(autoKeep.Maximum, Math.Max(autoKeep.Minimum, config.Keep));
            if (!config.LastAt.HasValue)
            {
                autoStatus.Text = "Todavía no se ha hecho ninguna copia automática en esta PC.";
                autoStatus.ForeColor = Theme.Muted;
            }
            else
            {
                var when = config.LastAt.Value.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                autoStatus.Text = config.LastOk
                    ? "Última copia: " + when + " · correcta"
                    : "Última copia: " + when + " · con error: " + config.LastMessage;
                autoStatus.ForeColor = config.LastOk ? Theme.Ink : Theme.Danger;
            }
            autoCard.Relayout();
        }

        // Ejecuta en el hilo de la interfaz si la vista sigue viva (los avisos de las tareas en segundo plano pasan por aquí).
        private void PostToUi(Action action)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) action(); })); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // Guarda casilla, carpeta y cantidad en esta PC. Si se activa con una carpeta inválida, no se activa y se explica en línea.
        // La validación toca el disco (una unidad de red caída puede tardar): se hace en segundo plano, nunca en el hilo de la interfaz.
        private void ApplyAutoBackupConfig(bool fromCheckbox)
        {
            var enabled = autoEnabled.Checked;
            var folder = autoFolder.Text;
            var keep = (int)autoKeep.Value;
            System.Threading.Tasks.Task.Run(() =>
            {
                Exception failure = null;
                try { AppData.SaveAutoBackupConfig(enabled, folder, keep); }
                catch (Exception ex) { failure = ex; }
                PostToUi(() => FinishApplyAutoBackupConfig(fromCheckbox, failure, folder, keep));
            });
        }

        private void FinishApplyAutoBackupConfig(bool fromCheckbox, Exception failure, string folder, int keep)
        {
            if (failure == null)
            {
                if (fromCheckbox || autoBanner.Tone == Tone.Warning) ShowAutoBanner(Tone.Info, null);
            }
            else if (failure is ArgumentException)
            {
                loading = true;
                try { autoEnabled.Checked = false; } finally { loading = false; }
                try { AppData.SaveAutoBackupConfig(false, folder, keep); } catch (Exception inner) { AppData.LogError(inner, "Guardar ajustes de copia automática"); }
                ShowAutoBanner(Tone.Warning, failure.Message + " La copia automática quedó desactivada.");
            }
            else
            {
                AppData.LogError(failure, "Guardar ajustes de copia automática");
                ShowAutoBanner(Tone.Danger, "No se pudieron guardar los ajustes de la copia automática en esta PC.");
            }
        }

        private void ChooseAutoBackupFolder_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Elija la carpeta donde se guardarán las copias (por ejemplo, una carpeta de OneDrive o Google Drive).", ShowNewFolderButton = true })
            {
                if (!string.IsNullOrWhiteSpace(autoFolder.Text)) dialog.SelectedPath = autoFolder.Text.Trim();   // sin comprobar si existe: no se toca el disco aquí
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                autoFolder.Text = dialog.SelectedPath;
                var chosen = dialog.SelectedPath;
                System.Threading.Tasks.Task.Run(() =>
                {
                    var problem = AppData.ValidateAutoBackupFolder(chosen);
                    PostToUi(() =>
                    {
                        if (problem != null) ShowAutoBanner(Tone.Warning, problem);
                        else ApplyAutoBackupConfig(true);
                    });
                });
            }
        }

        private void AutoBackupNow_Click(object sender, EventArgs e)
        {
            var enabled = autoEnabled.Checked;
            var folder = autoFolder.Text;
            var keep = (int)autoKeep.Value;
            autoNow.Enabled = false;
            ShowAutoBanner(Tone.Info, "Haciendo la copia en segundo plano…");
            // Todo el trabajo (validar la carpeta, guardar los ajustes y copiar) va fuera del hilo de la interfaz.
            System.Threading.Tasks.Task.Run(() =>
            {
                var problem = AppData.ValidateAutoBackupFolder(folder);
                if (problem != null)
                {
                    PostToUi(() => { autoNow.Enabled = true; ShowAutoBanner(Tone.Warning, problem); });
                    return;
                }
                // Guarda primero lo que se ve en pantalla; "ahora" funciona aunque la casilla esté apagada.
                try { AppData.SaveAutoBackupConfig(enabled, folder, keep); }
                catch (Exception ex)
                {
                    AppData.LogError(ex, "Guardar ajustes de copia automática");
                    PostToUi(() => { autoNow.Enabled = true; ShowAutoBanner(Tone.Danger, "No se pudieron guardar los ajustes de la copia automática en esta PC."); });
                    return;
                }
                var result = AppData.RunAutoBackup(true);
                PostToUi(() =>
                {
                    autoNow.Enabled = true;
                    // "Ya hay una copia en curso." (Ran = false) no es un error: es información.
                    if (!result.Ran) ShowAutoBanner(Tone.Info, result.Message);
                    else ShowAutoBanner(result.Ok ? Tone.Success : Tone.Danger, result.Ok ? result.Message : "No se pudo hacer la copia: " + result.Message);
                    try { RefreshAutoBackup(); } catch (Exception ex) { AppData.LogError(ex, "Actualizar estado de copia automática"); }
                });
            });
        }

        private void ExportBackup_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "Copia de Edén POS (*.edenbackup)|*.edenbackup",
                FileName = "EdenPOS-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".edenbackup",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            })
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    var exportedRows = AppData.ExportBackup(dialog.FileName);
                    Msg(Tone.Success, "Copia creada correctamente (" + exportedRows + " registros). Por seguridad, el token DNI y la cadena de conexión no fueron incluidos.", "Copia de seguridad");
                }
                catch (Exception ex)
                {
                    AppData.LogError(ex, "Exportar copia de seguridad");
                    Msg(Tone.Danger, ex is MySqlConnector.MySqlException ? AppData.DescribeDatabaseError((MySqlConnector.MySqlException)ex) : ex.Message, "No se pudo exportar");
                }
            }
        }

        private void ImportBackup_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Copia de Edén POS (*.edenbackup)|*.edenbackup|Todos los archivos (*.*)|*.*" })
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (MessageBox.Show(FindForm(), "Esto reemplazará todos los datos actuales (productos, recibos, turnos, usuarios y configuración) por los de la copia. ¿Continuar?", "Importar copia", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                // Tras CUALQUIER restauración (aunque un paso posterior haya fallado) sin usuario de sesión hay que volver al inicio de sesión.
                var restored = false;
                try
                {
                    var restoredRows = AppData.ImportBackup(dialog.FileName);
                    restored = true;
                    var notes = AppData.LastRestoreNotes;
                    var extra = notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes);
                    if (AppSession.CurrentUser == null)
                        Msg(Tone.Success, "Copia restaurada (" + restoredRows + " registros)." + extra, "Importación completa");
                    else
                    {
                        try { RefreshView(true); }
                        catch (Exception refreshError) { AppData.LogError(refreshError, "Actualizar Configuración tras restaurar la copia"); }
                        Msg(Tone.Success, "Copia restaurada correctamente (" + restoredRows + " registros). Configure el token DNI si esta es una computadora nueva." + extra, "Importación completa");
                    }
                }
                catch (AppData.BackupPostRestoreException ex)
                {
                    restored = true;
                    AppData.LogError(ex, "Importar copia de seguridad (pasos posteriores)");
                    var notes = AppData.LastRestoreNotes;
                    Msg(Tone.Warning, ex.Message + (notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes)), "Copia restaurada con avisos");
                }
                catch (Exception ex)
                {
                    AppData.LogError(ex, "Importar copia de seguridad");
                    Msg(Tone.Danger, ex is MySqlConnector.MySqlException ? AppData.DescribeDatabaseError((MySqlConnector.MySqlException)ex) : ex.Message, "No se pudo importar");
                }
                if (restored && AppSession.CurrentUser == null)
                {
                    var main = FindForm() as MainForm;
                    if (main != null) main.ForceLogout();
                }
            }
        }

        // ---- Réplica en Supabase ------------------------------------------------------------------------------------------
        // Nunca se muestra ni se registra la contraseña. Todo lo que conecta corre en Task.Run y vuelve con PostToUi.
        private void RelayoutSupabase()
        {
            if (sbConnCard == null) return;
            sbConnCard.Relayout();
            sbStatusCard.Relayout();
            sbRestoreCard.Relayout();
            SectionPage page;
            if (pages.TryGetValue("supabase", out page)) page.Relayout();
        }

        private static void SetBanner(Banner banner, Tone tone, string text)
        {
            banner.Tone = tone;
            banner.Text = text ?? "";
            banner.Visible = !string.IsNullOrEmpty(text);
        }

        // Muestra en pantalla lo guardado en esta PC. Si hay cambios sin guardar no se pisan (salvo force).
        private void RefreshSupabase(bool force)
        {
            if (sbConnCard == null) return;
            if (force) sbDirty = false;
            if (!sbDirty)
            {
                try
                {
                    var config = AppData.GetSupabaseReplicaConfig();
                    var wasLoading = loading;
                    loading = true;
                    try
                    {
                        sbHost.Text = config.Host ?? "";
                        sbPort.Value = Math.Min(sbPort.Maximum, Math.Max(sbPort.Minimum, config.Port));
                        sbDatabase.Text = string.IsNullOrEmpty(config.Database) ? "postgres" : config.Database;
                        sbUser.Text = config.User ?? "";
                        sbPassword.Text = "";
                        sbCa.Text = config.CaPath ?? "";
                        sbEnabled.Checked = config.Enabled;
                        sbHasPassword = config.HasPassword;
                        sbPasswordField.Placeholder = config.HasPassword ? "Guardada · deje vacío para conservarla" : "";
                    }
                    finally { loading = wasLoading; }
                }
                catch (Exception ex) { AppData.LogError(ex, "Leer la configuración de la réplica de Supabase"); }
            }
            RefreshSupabaseStatus();
        }

        private void OnSupabaseStatusChanged(object sender, EventArgs e)
        {
            // Llega desde un hilo de fondo.
            PostToUi(RefreshSupabaseStatus);
        }

        private void RefreshSupabaseStatus()
        {
            if (sbStatusCard == null) return;
            SupabaseReplicaStatus status;
            try { status = AppData.GetSupabaseReplicaStatus(); }
            catch (Exception ex) { AppData.LogError(ex, "Estado de la réplica de Supabase"); return; }

            switch (status.State)
            {
                case SupabaseReplicaState.UpToDate: sbPill.Text = "Al día"; sbPill.Tone = Tone.Success; break;
                case SupabaseReplicaState.Running: sbPill.Text = "Subiendo"; sbPill.Tone = Tone.Brand; break;
                case SupabaseReplicaState.Waiting: sbPill.Text = "Esperando"; sbPill.Tone = Tone.Info; break;
                case SupabaseReplicaState.Error: sbPill.Text = "Error"; sbPill.Tone = Tone.Danger; break;
                case SupabaseReplicaState.Blocked: sbPill.Text = "Bloqueada"; sbPill.Tone = Tone.Warning; break;
                default: sbPill.Text = "Desactivada"; sbPill.Tone = Tone.Neutral; break;
            }
            sbMessage.Text = status.Message ?? "";
            sbMessage.Visible = !string.IsNullOrEmpty(status.Message);
            sbLast.Text = "Última subida correcta: " + (status.LastSuccess.HasValue
                ? status.LastSuccess.Value.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "todavía ninguna");
            sbRows.Text = "Filas en la última pasada: " + (status.LastSuccess.HasValue ? status.LastPassRows.ToString() : "—");
            sbNext.Text = !status.Enabled ? "Próxima pasada: la réplica está desactivada en esta PC."
                : status.Running ? "Próxima pasada: en curso."
                : status.NextRun.HasValue ? "Próxima pasada: " + status.NextRun.Value.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "Próxima pasada: pendiente.";
            sbError.Text = "Último error: " + status.LastError;
            sbError.Visible = !string.IsNullOrWhiteSpace(status.LastError);
            if (status.OtherPcUploads)
                SetBanner(sbOtherPc, Tone.Warning, "Otra PC (" + status.RemoteLastPc + ") también sube datos a esta réplica. Active la réplica en una sola PC para que no se pisen.");
            else SetBanner(sbOtherPc, Tone.Warning, null);
            sbNow.Enabled = status.Enabled && !status.Running && !sbRestoring;
            sbPendingDeletes = status.PendingDeletes;
            SetBanner(sbBlockBanner, Tone.Warning, status.BlockedByMassDelete ? status.Message : null);
            sbForce.Visible = status.BlockedByMassDelete;
            sbForce.Enabled = status.BlockedByMassDelete && status.Enabled && !status.Running && !sbRestoring;
            RelayoutSupabase();
        }

        private void SupabaseChooseCa_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Certificados (*.crt;*.pem;*.cer)|*.crt;*.pem;*.cer|Todos los archivos (*.*)|*.*", Title = "Certificado CA de Supabase" })
            {
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) sbCa.Text = dialog.FileName;
            }
        }

        // Lee los campos. La contraseña vacía (null) conserva la guardada.
        private void ReadSupabaseInputs(out string host, out int port, out string database, out string user, out string password, out string ca)
        {
            host = sbHost.Text.Trim();
            port = (int)sbPort.Value;
            database = sbDatabase.Text.Trim();
            user = sbUser.Text.Trim();
            password = sbPassword.TextLength == 0 ? null : sbPassword.Text;
            ca = sbCa.Text.Trim();
        }

        private void SupabaseTest_Click(object sender, EventArgs e)
        {
            string host, database, user, password, ca; int port;
            ReadSupabaseInputs(out host, out port, out database, out user, out password, out ca);
            sbTest.Enabled = false;
            SetBanner(sbConnBanner, Tone.Info, "Probando la conexión con Supabase…");
            RelayoutSupabase();
            System.Threading.Tasks.Task.Run(() =>
            {
                string ok = null; Exception failure = null;
                try { ok = AppData.TestSupabaseConnection(host, port, database, user, password, ca); }
                catch (Exception ex) { failure = ex; }
                PostToUi(() =>
                {
                    sbTest.Enabled = true;
                    if (failure == null) SetBanner(sbConnBanner, Tone.Success, ok);
                    else if (failure is ArgumentException) SetBanner(sbConnBanner, Tone.Warning, failure.Message);
                    else
                    {
                        AppData.LogError(failure, "Probar la conexión con Supabase");
                        SetBanner(sbConnBanner, Tone.Danger, failure.Message);
                    }
                    RelayoutSupabase();
                });
            });
        }

        private void SupabaseSave_Click(object sender, EventArgs e)
        {
            string host, database, user, password, ca; int port;
            ReadSupabaseInputs(out host, out port, out database, out user, out password, out ca);
            var enabled = sbEnabled.Checked;
            string defaultCa = null;
            try { defaultCa = AppData.GetSupabaseReplicaConfig().DefaultCaPath; } catch { }
            if (!string.IsNullOrEmpty(defaultCa) && string.Equals(ca, defaultCa, StringComparison.OrdinalIgnoreCase)) ca = "";   // usa siempre el del programa
            sbSave.Enabled = false;
            SetBanner(sbConnBanner, Tone.Info, "Guardando…");
            RelayoutSupabase();
            System.Threading.Tasks.Task.Run(() =>
            {
                Exception failure = null;
                try { AppData.SaveSupabaseReplicaConfig(enabled, host, port, database, user, password, ca); }
                catch (Exception ex) { failure = ex; }
                PostToUi(() =>
                {
                    sbSave.Enabled = true;
                    if (failure == null)
                    {
                        RefreshSupabase(true);
                        SetBanner(sbConnBanner, Tone.Success, enabled
                            ? "Configuración guardada. La réplica quedó activada en esta PC y subirá los datos en unos instantes."
                            : "Configuración guardada. La réplica está desactivada en esta PC.");
                    }
                    else if (failure is ArgumentException) SetBanner(sbConnBanner, Tone.Warning, failure.Message);
                    else
                    {
                        AppData.LogError(failure, "Guardar la configuración de la réplica de Supabase");
                        SetBanner(sbConnBanner, Tone.Danger, "No se pudo guardar la configuración de la réplica en esta PC.");
                    }
                    RelayoutSupabase();
                });
            });
        }

        private void SupabaseNow_Click(object sender, EventArgs e)
        {
            RunSupabaseNow(false);
        }

        private void SupabaseForce_Click(object sender, EventArgs e)
        {
            var pending = sbPendingDeletes;
            if (MessageBox.Show(FindForm(), "Se borrarán " + pending + " filas en Supabase para que la réplica coincida con esta PC. Esto no se puede deshacer desde EdenPOS. ¿Subir de todos modos?",
                    "Subir de todos modos", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            RunSupabaseNow(true);
        }

        private void RunSupabaseNow(bool allowMassDelete)
        {
            sbNow.Enabled = false;
            sbForce.Enabled = false;
            SetBanner(sbRunBanner, Tone.Info, "Subiendo los cambios a Supabase…");
            RelayoutSupabase();
            System.Threading.Tasks.Task.Run(() =>
            {
                ReplicaResult result = null; Exception failure = null;
                try { result = allowMassDelete ? AppData.RunSupabaseReplicaNow(true) : AppData.RunSupabaseReplicaNow(); }
                catch (Exception ex) { failure = ex; }
                PostToUi(() =>
                {
                    if (failure != null)
                    {
                        AppData.LogError(failure, "Subir ahora a Supabase");
                        SetBanner(sbRunBanner, Tone.Danger, "No se pudo subir: " + failure.Message);
                    }
                    else if (result.Skipped) SetBanner(sbRunBanner, Tone.Info, string.IsNullOrEmpty(result.Message) ? "Ya había una subida en curso." : result.Message);
                    else if (result.Blocked) SetBanner(sbRunBanner, Tone.Warning, result.Message);
                    else if (result.Ok) SetBanner(sbRunBanner, Tone.Success, "Subida completa: " + result.TotalRows + " filas.");
                    else SetBanner(sbRunBanner, Tone.Danger, "No se pudo subir: " + result.Message);
                    RefreshSupabaseStatus();
                });
            });
        }

        private void SupabaseRestore_Click(object sender, EventArgs e)
        {
            if (sbRestoring) return;
            var owner = FindForm();
            // Primero se consulta qué hay en Supabase (en segundo plano) para confirmar con datos a la vista.
            sbRestoring = true;
            sbRestore.Enabled = false;
            sbNow.Enabled = false;
            sbForce.Enabled = false;
            SetBanner(sbRestoreBanner, Tone.Info, "Consultando qué hay en Supabase…");
            RelayoutSupabase();
            System.Threading.Tasks.Task.Run(() =>
            {
                SupabaseRestorePreview preview = null; Exception previewFailure = null;
                try { preview = AppData.GetSupabaseRestorePreview(); }
                catch (Exception ex) { previewFailure = ex; }
                PostToUi(() => ContinueSupabaseRestore(owner, preview, previewFailure));
            });
        }

        private void ContinueSupabaseRestore(Form owner, SupabaseRestorePreview preview, Exception previewFailure)
        {
            sbRestoring = false;
            sbRestore.Enabled = true;
            SetBanner(sbRestoreBanner, Tone.Info, null);
            if (previewFailure != null || preview == null || !preview.CanRestore)
            {
                if (previewFailure != null) AppData.LogError(previewFailure, "Consultar la réplica antes de restaurar");
                var problem = previewFailure != null ? "No se pudo consultar Supabase: " + previewFailure.Message
                    : preview == null ? "No se pudo consultar Supabase." : preview.Problem;
                if (string.IsNullOrWhiteSpace(problem)) problem = "La réplica de Supabase no se puede restaurar.";
                Theme.Message(owner, Tone.Warning, problem, "Restaurar desde Supabase");
                RefreshSupabaseStatus();
                return;
            }
            RefreshSupabaseStatus();

            if (MessageBox.Show(owner, "Esto reemplazará todos los datos de esta PC (productos, recibos, turnos, usuarios y configuración) por los que están en Supabase. Antes se guardará una copia local automática.\n\n" +
                    DescribeRestorePreview(preview) + "\n\n¿Continuar?", "Restaurar desde Supabase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (!ConfirmTyped(owner, "Restaurar desde Supabase", "Última confirmación: los datos actuales de esta PC se reemplazarán. Esta acción no se puede deshacer, salvo con la copia local previa.", "RESTAURAR")) return;

            sbRestoring = true;
            sbRestore.Enabled = false;
            sbNow.Enabled = false;
            sbForce.Enabled = false;
            SetBanner(sbRestoreBanner, Tone.Info, "Restaurando…");
            RelayoutSupabase();
            if (owner != null) { owner.Enabled = false; owner.UseWaitCursor = true; }
            System.Threading.Tasks.Task.Run(() =>
            {
                var rows = 0; Exception failure = null;
                try { rows = AppData.RestoreFromSupabase(); }
                catch (Exception ex) { failure = ex; }
                PostToUi(() => FinishSupabaseRestore(owner, rows, failure));
            });
        }

        // Resumen para la confirmación: última subida (fecha y PC) y filas de las tablas principales.
        private static string DescribeRestorePreview(SupabaseRestorePreview preview)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string>();
            lines.Add("Última subida en Supabase: " + (preview.LastUpload.HasValue ? preview.LastUpload.Value.ToString("dd/MM/yyyy HH:mm", inv) : "fecha desconocida") +
                      (string.IsNullOrWhiteSpace(preview.LastPc) ? "" : " desde la PC " + preview.LastPc) + ".");
            var rows = preview.RowsPerTable ?? new Dictionary<string, int>();
            Func<string, int> count = name => { int n; return rows.TryGetValue(name, out n) ? n : 0; };
            lines.Add("Recibos: " + count("recibo") + " · Productos: " + count("producto") + " · Clientes: " + count("cliente") +
                      " · Usuarios: " + count("usuario") + " · Turnos: " + count("sesion_caja"));
            var total = 0;
            foreach (var pair in rows) total += pair.Value;
            lines.Add("Total de filas: " + total + ".");
            return string.Join("\n", lines);
        }

        private void FinishSupabaseRestore(Form owner, int rows, Exception failure)
        {
            if (owner != null && !owner.IsDisposed) { owner.Enabled = true; owner.UseWaitCursor = false; }
            sbRestoring = false;
            sbRestore.Enabled = true;
            SetBanner(sbRestoreBanner, Tone.Info, null);
            var restored = false;
            var post = failure as AppData.BackupPostRestoreException;
            if (failure == null || post != null) restored = true;

            if (failure == null)
            {
                var notes = AppData.LastRestoreNotes;
                var extra = notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes);
                if (AppSession.CurrentUser == null)
                    Msg(Tone.Success, "Datos restaurados desde Supabase (" + rows + " filas)." + extra, "Restauración completa");
                else
                {
                    try { RefreshView(true); }
                    catch (Exception refreshError) { AppData.LogError(refreshError, "Actualizar Configuración tras restaurar desde Supabase"); }
                    Msg(Tone.Success, "Datos restaurados desde Supabase (" + rows + " filas). Configure el token DNI si esta es una computadora nueva." + extra, "Restauración completa");
                }
            }
            else if (post != null)
            {
                AppData.LogError(post, "Restaurar desde Supabase (pasos posteriores)");
                var notes = AppData.LastRestoreNotes;
                Msg(Tone.Warning, "Los datos se restauraron, pero con avisos: " + post.Message + (notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes)), "Restaurado con avisos");
            }
            else
            {
                AppData.LogError(failure, "Restaurar desde Supabase");
                SetBanner(sbRestoreBanner, Tone.Danger, "No se pudo restaurar: " + (failure is MySqlConnector.MySqlException ? AppData.DescribeDatabaseError((MySqlConnector.MySqlException)failure) : failure.Message));
            }
            RefreshSupabaseStatus();
            if (restored && AppSession.CurrentUser == null)
            {
                var main = owner as MainForm;
                if (main != null) main.ForceLogout();
            }
        }

        // Segunda confirmación: hay que escribir la palabra exacta. Devuelve true si la dueña confirma.
        private static bool ConfirmTyped(IWin32Window owner, string title, string text, string word)
        {
            const int width = 480;
            var pad = Theme.Space.Xl;
            var inner = width - 2 * pad;
            using (var dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Surface,
                Font = Theme.Body,
                ClientSize = new Size(width, 250)
            })
            {
                var message = new Label { Text = text, Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = false, Bounds = new Rectangle(pad, pad, inner, 72) };
                var label = Theme.LabelField("Escriba " + word + " para confirmar");
                label.Location = new Point(pad, 108);
                var box = new TextBox { MaxLength = 20 };
                var field = Theme.Field(box);
                field.Bounds = new Rectangle(pad, 134, inner, Theme.Dim.Field);
                var ok = Theme.Button("Restaurar", ButtonVariant.Destructive);
                ok.Enabled = false; ok.DialogResult = DialogResult.OK;
                var cancel = Theme.Button("Cancelar", ButtonVariant.Secondary);
                cancel.DialogResult = DialogResult.Cancel;
                ok.Size = new Size(140, Theme.Dim.Field); cancel.Size = new Size(140, Theme.Dim.Field);
                var y = 250 - pad - Theme.Dim.Field;
                ok.Location = new Point(width - pad - 140, y);
                cancel.Location = new Point(width - pad - 140 - Theme.Space.Sm - 140, y);
                box.TextChanged += (s, e) => ok.Enabled = string.Equals(box.Text.Trim(), word, StringComparison.Ordinal);
                dialog.Controls.AddRange(new Control[] { message, label, field, cancel, ok });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => box.Focus();
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }

        private void TogglePortable_Click(object sender, EventArgs e)
        {
            var enable = !AppData.IsPortableMode;
            var message = enable
                ? "Se copiarán los datos junto al ejecutable. Use esta opción solamente si la carpeta del programa es escribible. ¿Activar el modo portable?"
                : "Los datos se copiarán al perfil local de Windows. ¿Desactivar el modo portable?";
            if (MessageBox.Show(FindForm(), message, "Modo portable", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                AppData.SetPortableMode(enable);
                RefreshView();
                Msg(Tone.Info, "Modo de almacenamiento actualizado. Reinicie Edén POS antes de continuar trabajando.", "Edén POS");
            }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Cambiar modo portable");
                Msg(Tone.Danger, ex.Message, "Modo portable");
            }
        }

        // Solo guarda las promociones (promo_*); el resto de la configuración se conserva tal como está en la base.
        private void SavePromotions_Click(object sender, EventArgs e)
        {
            AppSettings settings;
            try
            {
                settings = AppData.Settings;
                settings.PromoSextaVisitaActiva = promoSexta.Checked;
                settings.PromoGrupalMinimoPersonas = (int)promoMinimo.Value;
                settings.PromoGrupalPorcentaje = Math.Round(promoPorcentaje.Value, 2);
                AppData.SaveSettings(settings);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Msg(Tone.Warning, ex.Message, "Promociones");
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Guardar promociones (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Promociones");
                return;
            }
            promoDirty = false;
            Msg(Tone.Success, "Promociones guardadas.", "Edén POS");
        }

        private void Save_Click(object sender, EventArgs e)
        {
            if (!Validation.IsValidRequiredText(store.Text, 3, 80))
            {
                ShowSection("negocio");
                Msg(Tone.Warning, "El nombre del negocio debe tener entre 3 y 80 caracteres.", "Configuración");
                store.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(currency.Text) || currency.Text.Length > 5 || currency.Text.Any(char.IsControl))
            {
                ShowSection("negocio");
                Msg(Tone.Warning, "El símbolo de moneda debe tener entre 1 y 5 caracteres.", "Configuración");
                currency.Focus();
                return;
            }
            var environmentToken = Environment.GetEnvironmentVariable("EDENPOS_DNI_TOKEN");
            if (dniEnabled.Checked && string.IsNullOrWhiteSpace(environmentToken) && string.IsNullOrWhiteSpace(dniToken.Text))
            {
                ShowSection("dni");
                Msg(Tone.Warning, "Para activar la consulta de DNI debe ingresar un token de ApiPeruDev.", "Configuración de DNI");
                dniToken.Focus();
                return;
            }
            AppSettings settings;
            try
            {
                settings = AppData.Settings;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Guardar configuración: leer (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Configuración");
                return;
            }
            settings.StoreName = store.Text.Trim().ToUpperInvariant();
            settings.CurrencySymbol = currency.Text;
            settings.DniLookupEnabled = dniEnabled.Checked;
            if (string.IsNullOrWhiteSpace(environmentToken)) settings.DniApiTokenProtected = AppData.ProtectSecret(dniToken.Text);
            try
            {
                AppData.SaveSettings(settings);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Msg(Tone.Warning, ex.Message, "Configuración");
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Guardar configuración (base de datos)");
                Msg(Tone.Danger, AppData.DescribeDatabaseError(ex), "Configuración");
                return;
            }
            businessDirty = dniDirty = false;
            loading = true;
            try { store.Text = settings.StoreName; }
            finally { loading = false; }
            Msg(Tone.Success, "Configuración guardada.", "Edén POS");
        }

        // ================================================================================================================
        // Controles propios de esta pantalla (no están en Theme.cs/Controls.cs)
        // ================================================================================================================

        // Tarjeta que apila sus hijos en vertical: cada uno ocupa el ancho interior (o un ancho fijo) y su alto natural.
        // Los textos largos se ajustan en varias líneas. Al cambiar el ancho recalcula su alto.
        private sealed class StackCard : CardPanel
        {
            private sealed class Entry { public Control Control; public int Gap; public int FixedWidth; }
            private readonly List<Entry> entries = new List<Entry>();
            private bool stacking;
            private const int PadX = Theme.Space.Xl;
            private const int PadY = Theme.Space.Xl;

            public StackCard()
            {
                Padding = new Padding(PadX, PadY, PadX, PadY);
            }

            public void Add(Control control, int gapBefore, int fixedWidth = 0, Font font = null)
            {
                if (font != null) control.Font = font;
                entries.Add(new Entry { Control = control, Gap = gapBefore, FixedWidth = fixedWidth });
                Controls.Add(control);
                Restack();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Restack();
            }

            public void Relayout() { Restack(); }

            private void Restack()
            {
                if (stacking || Width <= 2 * PadX) return;
                stacking = true;
                try
                {
                    var inner = Width - 2 * PadX;
                    var y = PadY;
                    foreach (var entry in entries)
                    {
                        var c = entry.Control;
                        if (!c.Visible) continue;
                        var w = entry.FixedWidth > 0 ? Math.Min(entry.FixedWidth, inner) : inner;
                        int h;
                        if (c is Label)
                        {
                            c.AutoSize = false;
                            h = Math.Max(c.Font.Height, c.GetPreferredSize(new Size(w, 0)).Height);
                        }
                        else if (c is FlowLayoutPanel)
                        {
                            c.Width = w;
                            h = c.GetPreferredSize(new Size(w, 0)).Height;
                        }
                        else if (c is Banner)
                        {
                            c.Width = w;
                            h = c.Height;
                        }
                        else h = c.Height;
                        y += entry.Gap;
                        c.SetBounds(PadX, y, w, h);
                        y += h;
                    }
                    var total = y + PadY;
                    if (Height != total) Height = total;
                }
                finally { stacking = false; }
            }
        }

        // Página de una sección: apila sus tarjetas con ancho máximo y desplazamiento vertical propio.
        // FillHeight = la tarjeta ocupa todo el alto disponible (tabla de usuarios).
        private sealed class SectionPage : Panel
        {
            private readonly List<CardPanel> cards = new List<CardPanel>();
            public bool FillHeight { get; set; }
            private bool laying;

            public SectionPage()
            {
                AutoScroll = true;
                BackColor = Theme.Canvas;
                Padding = new Padding(Theme.Space.Xxl, Theme.Space.Xl, Theme.Space.Xxl, Theme.Space.Xxl);
                DoubleBuffered = true;
            }

            public void AddCard(CardPanel card) { cards.Add(card); Controls.Add(card); }

            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Relayout(); }
            protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }

            public void Relayout()
            {
                if (laying || !Visible || ClientSize.Width <= 0) return;
                laying = true;
                try
                {
                    var available = ClientSize.Width - Padding.Horizontal - (FillHeight ? 0 : SystemInformation.VerticalScrollBarWidth);
                    var width = Math.Max(360, Math.Min(840, available));
                    var offsetX = AutoScrollPosition.X;
                    var offsetY = AutoScrollPosition.Y;
                    if (FillHeight)
                    {
                        var card = cards[0];
                        var height = Math.Max(480, ClientSize.Height - Padding.Vertical);
                        card.SetBounds(Padding.Left + offsetX, Padding.Top + offsetY, width, height);
                        AutoScrollMinSize = new Size(0, Padding.Top + height + Padding.Bottom);
                        return;
                    }
                    var y = Padding.Top;
                    foreach (var card in cards)
                    {
                        card.Width = width;
                        card.Location = new Point(Padding.Left + offsetX, y + offsetY);
                        y += card.Height + Theme.Space.Xl;
                    }
                    AutoScrollMinSize = new Size(0, y - Theme.Space.Xl + Padding.Bottom);
                }
                finally { laying = false; }
            }
        }

        // Ítem de la navegación: texto a la izquierda, seleccionado = fondo verde suave + marca de acento; teclado con flechas.
        private sealed class NavItem : Control
        {
            private bool selected, hover;
            public event EventHandler Activated;
            public event EventHandler<int> StepRequested;

            public NavItem()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                Margin = Padding.Empty;
            }

            public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

            protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 2, Width - 1, Height - 5);
                if (selected || hover)
                    using (var path = Gfx.Round(rect, Theme.Radius.Control))
                    using (var brush = new SolidBrush(selected ? Theme.GreenSoft : Theme.SurfaceSunken))
                        g.FillPath(brush, path);
                if (selected)
                    using (var path = Gfx.Round(new Rectangle(0, rect.Top + 10, 4, rect.Height - 20), 2))
                    using (var brush = new SolidBrush(Theme.Primary))
                        g.FillPath(brush, path);
                if (Focused && ShowFocusCues)
                    using (var path = Gfx.Round(new Rectangle(1, 3, Width - 3, Height - 7), Theme.Radius.Control))
                    using (var pen = new Pen(Theme.Primary, 2f))
                        g.DrawPath(pen, path);
                TextRenderer.DrawText(g, Text, selected ? Theme.Subhead : Theme.Body, new Rectangle(Theme.Space.Lg, 0, Math.Max(0, Width - Theme.Space.Lg - Theme.Space.Sm), Height),
                    selected ? Theme.GreenDark : Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            // Respuesta inmediata: se activa al presionar, no al soltar.
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left) { Focus(); var h = Activated; if (h != null) h(this, EventArgs.Empty); }
            }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                var step = StepRequested;
                if (e.KeyCode == Keys.Down && step != null) { step(this, 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up && step != null) { step(this, -1); e.Handled = true; }
                else if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Activated != null) { Activated(this, EventArgs.Empty); e.Handled = true; }
            }
        }
    }
}
