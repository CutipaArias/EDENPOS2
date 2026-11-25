using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EdenPOS
{
    // RF-21: boletas (simuladas) no enviadas por falta de conexión o por rechazo, con reintento manual de una o de todas.
    // El reintento automático sigue a cargo de ElectronicInvoicingService (cada 5 minutos, hasta MaxIntentos). Este diálogo solo reutiliza
    // AppData.ProcesarComprobante (una) y AppData.ReintentarComprobantes (todas, ignorando el tope) y se refresca con ComprobantesChanged.
    // Se abre desde Recibos y desde Configuración > Facturación electrónica (simulada).
    public sealed class PendingInvoicesDialog : Form
    {
        private readonly DataGridView grid;
        private readonly EmptyState emptyState;
        private readonly Label summary;
        private readonly Button retryOne;
        private readonly Button retryAll;
        private bool busy;

        public PendingInvoicesDialog()
        {
            Text = "Boletas no enviadas (simuladas)";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1080, 600);
            MinimumSize = new Size(760, 440);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Surface;
            WindowChrome.Integrate(this);
            Font = Theme.Body;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Lg), BackColor = Theme.Surface };
            var tableCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            grid = new DataGridView { Dock = DockStyle.Fill, MultiSelect = false };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Doc", HeaderText = "Boleta", Width = 126, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Font = Theme.Subhead } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "Fecha", Width = 132, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Customer", HeaderText = "Cliente", FillWeight = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "Total", Width = 104, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Estado", Width = 104, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tries", HeaderText = "Intentos", Width = 88, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Last", HeaderText = "Último intento", Width = 132, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Response", HeaderText = "Último código / mensaje", FillWeight = 160 });
            grid.SelectionChanged += (s, e) => UpdateButtons();
            emptyState = Theme.EmptyState("No hay boletas pendientes", "Todas las boletas simuladas fueron aceptadas o anuladas.");
            emptyState.Visible = false;
            tableCard.Controls.Add(emptyState);
            tableCard.Controls.Add(grid);
            host.Controls.Add(tableCard);

            var close = Theme.Button("Cerrar", ButtonVariant.Secondary);
            close.DialogResult = DialogResult.Cancel;
            retryOne = Theme.Button("Reintentar este", ButtonVariant.Secondary);
            retryOne.Click += async (s, e) => await RetrySelectedAsync();
            retryAll = Theme.Button("Reintentar todos", ButtonVariant.Primary);
            retryAll.Click += async (s, e) => await RetryAllAsync();
            var actions = DialogKit.Actions(null, retryAll, retryOne, close);

            var info = new Panel { Dock = DockStyle.Top, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Md), BackColor = Theme.Surface };
            var banner = Theme.Banner("Simulado: no se envía nada a SUNAT ni a ningún OSE. Los pendientes y rechazados se reintentan solos cada " +
                (int)ElectronicInvoicingService.IntervaloPorDefecto.TotalMinutes + " minutos mientras la app está abierta (máximo " +
                ElectronicInvoicingService.MaxIntentos + " intentos automáticos); aquí puede reintentarlos cuando quiera.", Tone.Info);
            banner.Dock = DockStyle.Top;
            summary = new Label { Dock = DockStyle.Top, Height = Theme.Body.Height + Theme.Space.Md, Font = Theme.Body, ForeColor = Theme.Muted, TextAlign = ContentAlignment.BottomLeft };
            info.Controls.Add(summary);
            info.Controls.Add(banner);
            // El aviso cambia de alto con el ancho del diálogo: el panel lo acompaña.
            Action fitInfo = () => info.Height = banner.Height + summary.Height + info.Padding.Vertical;
            banner.SizeChanged += (s, e) => fitInfo();
            fitInfo();

            Controls.Add(host);
            Controls.Add(actions);
            Controls.Add(info);
            Controls.Add(DialogKit.Header("Boletas no enviadas", "Pendientes por falta de conexión o rechazadas por el emisor simulado."));
            CancelButton = close;
            Theme.FitDialogToWorkingArea(this);

            LoadRows();
            AppData.ComprobantesChanged += OnComprobantesChanged;
            FormClosed += (s, e) => AppData.ComprobantesChanged -= OnComprobantesChanged;
        }

        // Puede llegar desde un hilo de fondo (reintento automático): se pasa al hilo de la interfaz.
        private void OnComprobantesChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed && !busy) LoadRows(); })); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void LoadRows()
        {
            List<Comprobante> list;
            try
            {
                list = AppData.GetComprobantesNoEnviados();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Boletas no enviadas (base de datos)");
                summary.Text = AppData.DescribeDatabaseError(ex);
                summary.ForeColor = Theme.Danger;
                return;
            }
            var selectedId = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as string : null;
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var c in list.OrderBy(x => x.FechaEmision).ThenBy(x => x.Numero))
            {
                var response = string.IsNullOrEmpty(c.CodigoRespuesta) && string.IsNullOrEmpty(c.MensajeRespuesta)
                    ? (c.Intentos == 0 ? "Aún sin enviar" : "Sin respuesta")
                    : (c.CodigoRespuesta ?? "") + (string.IsNullOrEmpty(c.MensajeRespuesta) ? "" : (string.IsNullOrEmpty(c.CodigoRespuesta) ? "" : " · ") + c.MensajeRespuesta);
                var index = grid.Rows.Add(c.Documento, c.FechaEmision.ToString("dd/MM/yyyy HH:mm"),
                    (c.CustomerName ?? "") + (string.IsNullOrEmpty(c.CustomerDni) ? "" : "  ·  DNI " + c.CustomerDni),
                    Theme.Money(c.Total), c.Estado,
                    c.Intentos + (c.Intentos >= ElectronicInvoicingService.MaxIntentos ? " (tope)" : ""),
                    c.UltimoIntento.HasValue ? c.UltimoIntento.Value.ToString("dd/MM/yyyy HH:mm") : "—", response);
                var row = grid.Rows[index];
                row.Tag = c.Id;
                row.Cells["State"].Style.ForeColor = c.Estado == ComprobanteEstados.Rechazado ? Theme.Danger : Theme.Warning;
                if (c.Id == selectedId) row.Selected = true;
            }
            grid.ResumeLayout();
            var rejected = list.Count(x => x.Estado == ComprobanteEstados.Rechazado);
            summary.ForeColor = Theme.Muted;
            summary.Text = list.Count == 0 ? "No hay boletas por reenviar."
                : list.Count + (list.Count == 1 ? " boleta no enviada" : " boletas no enviadas") + ": " + (list.Count - rejected) + " pendiente(s) y " + rejected + " rechazada(s).";
            grid.Visible = list.Count > 0;
            emptyState.Visible = list.Count == 0;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            retryAll.Enabled = !busy && grid.Rows.Count > 0;
            retryOne.Enabled = !busy && grid.SelectedRows.Count > 0;
        }

        private async Task RetrySelectedAsync()
        {
            if (busy || grid.SelectedRows.Count == 0) return;
            var id = grid.SelectedRows[0].Tag as string;
            var doc = Convert.ToString(grid.SelectedRows[0].Cells["Doc"].Value);
            if (id == null) return;
            await RunAsync(() =>
            {
                var estado = AppData.ProcesarComprobante(id, true);
                return estado == null ? "La boleta " + doc + " ya no estaba pendiente (se aceptó, se anuló o la envió otra caja)."
                    : estado == ComprobanteEstados.Aceptado ? "Boleta " + doc + " aceptada (simulado)."
                    : "Boleta " + doc + " rechazada otra vez (simulado). Revise el código y el mensaje.";
            });
        }

        private async Task RetryAllAsync()
        {
            if (busy) return;
            await RunAsync(() =>
            {
                var r = AppData.ReintentarComprobantes(true);
                return r.YaEnCurso ? "Ya hay un reintento en curso. Inténtelo de nuevo en unos segundos."
                    : r.Procesados == 0 ? "No había boletas pendientes ni rechazadas."
                    : "Reintento simulado terminado: " + r.Aceptados + " aceptada(s), " + r.Rechazados + " rechazada(s).";
            });
        }

        // El envío corre fuera del hilo de la interfaz; al terminar se recarga la lista y se informa el resultado.
        private async Task RunAsync(Func<string> work)
        {
            busy = true;
            UpdateButtons();
            UseWaitCursor = true;
            string message = null;
            var tone = Tone.Info;
            try
            {
                message = await Task.Run(work);
            }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Reintentar boletas no enviadas");
                tone = Tone.Warning;
                message = ex is MySqlConnector.MySqlException ? AppData.DescribeDatabaseError((MySqlConnector.MySqlException)ex)
                    : "No se pudieron reintentar los envíos. El detalle quedó en errors.log.";
            }
            finally
            {
                busy = false;
                UseWaitCursor = false;
            }
            if (IsDisposed) return;
            LoadRows();
            if (message != null) Theme.Message(this, tone, message, "Boletas no enviadas (simuladas)");
        }
    }
}
