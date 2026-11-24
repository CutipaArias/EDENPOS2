using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    // RNF-09 / RN-14: Configuración > Clientes (solo la dueña). Busca un cliente registrado y registra que retiró su consentimiento:
    // AppData.RevokeCustomerConsent borra su teléfono y anonimiza su DNI donde se puede (ver CustomerPrivacy.cs).
    public sealed class CustomerPrivacyDialog : Form
    {
        private readonly DataGridView grid;
        private readonly TextBox searchBox;
        private readonly EmptyState emptyState;
        private readonly Button revoke;
        private List<Customer> customers = new List<Customer>();

        public CustomerPrivacyDialog()
        {
            Text = "Clientes";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(720, 440);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Surface;
            Font = Theme.Body;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Lg), BackColor = Theme.Surface };
            var tableCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            grid = new DataGridView { Dock = DockStyle.Fill, MultiSelect = false };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Cliente", FillWeight = 150 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dni", HeaderText = "DNI", Width = 120, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Phone", HeaderText = "Teléfono", Width = 120, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Consent", HeaderText = "Consentimiento", FillWeight = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Origen", Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.SelectionChanged += (s, e) => UpdateButtons();
            emptyState = Theme.EmptyState("No hay clientes", "Los clientes se registran en Ventas, con su consentimiento.");
            emptyState.Visible = false;
            tableCard.Controls.Add(emptyState);
            tableCard.Controls.Add(grid);
            host.Controls.Add(tableCard);

            var close = Theme.Button("Cerrar", ButtonVariant.Secondary);
            close.DialogResult = DialogResult.Cancel;
            revoke = Theme.Button("Retirar consentimiento", ButtonVariant.Destructive);
            revoke.Click += (s, e) => RevokeSelected();
            var actions = DialogKit.Actions(revoke, close);

            var searchPanel = new Panel { Dock = DockStyle.Top, Height = Theme.Dim.Field + Theme.Space.Md, Padding = new Padding(Theme.Space.Xl, 0, Theme.Space.Xl, Theme.Space.Md), BackColor = Theme.Surface };
            searchBox = new TextBox { Font = Theme.Body, Tag = "Buscar por nombre o DNI", Dock = DockStyle.Fill, AccessibleName = "Buscar por nombre o DNI" };
            var searchField = Theme.Field(searchBox, Theme.Dim.Field);
            searchField.Dock = DockStyle.Fill;
            searchBox.TextChanged += (s, e) => Fill();
            searchPanel.Controls.Add(searchField);

            Controls.Add(host);
            Controls.Add(actions);
            Controls.Add(searchPanel);
            Controls.Add(DialogKit.Header("Clientes",
                "Si un cliente retira su consentimiento (Ley N.º 29733), se borra su teléfono y se anonimiza su DNI."));
            CancelButton = close;
            Theme.FitDialogToWorkingArea(this);
            Reload();
        }

        private void Reload()
        {
            try
            {
                customers = AppData.Customers;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Clientes (base de datos)");
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Clientes");
                customers = new List<Customer>();
            }
            Fill();
        }

        private static string DniText(Customer customer)
        {
            return AppData.IsAnonymizedDni(customer.Dni) ? "Anonimizado" : customer.Dni;
        }

        private static string ConsentText(Customer customer)
        {
            if (customer.ConsentRevokedAt.HasValue) return "Retirado el " + customer.ConsentRevokedAt.Value.ToString("dd/MM/yyyy HH:mm");
            if (customer.DataConsent) return "Sí" + (customer.ConsentDate.HasValue ? ", desde el " + customer.ConsentDate.Value.ToString("dd/MM/yyyy") : "");
            return "No registrado";
        }

        private void Fill()
        {
            var selected = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Customer : null;
            var term = (searchBox.Text ?? "").Trim();
            var query = customers.AsEnumerable();
            if (term.Length > 0)
                query = query.Where(c => (c.FullName ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                         (!AppData.IsAnonymizedDni(c.Dni) && (c.Dni ?? "").Contains(term)));
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var customer in query.OrderBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase))
            {
                var index = grid.Rows.Add(customer.FullName, DniText(customer), string.IsNullOrEmpty(customer.Phone) ? "—" : customer.Phone,
                    ConsentText(customer), customer.Source);
                var row = grid.Rows[index];
                row.Tag = customer;
                if (customer.ConsentRevokedAt.HasValue) row.DefaultCellStyle.ForeColor = Theme.Muted;
                if (selected != null && customer.Dni == selected.Dni) row.Selected = true;
            }
            grid.ResumeLayout();
            grid.Visible = grid.Rows.Count > 0;
            emptyState.Visible = grid.Rows.Count == 0;
            if (term.Length > 0) { emptyState.Title = "Sin resultados"; emptyState.Hint = "Pruebe con otro nombre o DNI."; }
            else { emptyState.Title = "No hay clientes"; emptyState.Hint = "Los clientes se registran en Ventas, con su consentimiento."; }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var customer = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Customer : null;
            revoke.Enabled = customer != null && !customer.ConsentRevokedAt.HasValue;
        }

        private void RevokeSelected()
        {
            var customer = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Customer : null;
            if (customer == null || customer.ConsentRevokedAt.HasValue) return;
            var confirm = MessageBox.Show(this,
                "El cliente " + customer.FullName + " retira su consentimiento para el uso de sus datos.\n\n" +
                "• Se borra su teléfono.\n" +
                "• Su DNI se anonimiza en el registro de clientes, en los tickets abiertos y en los recibos de " + Theme.Money(AppData.DniObligatorioDesde) + " o menos.\n" +
                "• Los recibos mayores a " + Theme.Money(AppData.DniObligatorioDesde) + " conservan el documento, porque SUNAT lo exige en esas boletas.\n" +
                "• Su conteo de visitas para la promoción de sexta visita vuelve a empezar.\n\n" +
                "Esta acción no se puede deshacer y queda en Auditoría. ¿Continuar?",
                "Retirar consentimiento", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
            ConsentRevocationResult result;
            try
            {
                result = AppData.RevokeCustomerConsent(customer.Dni);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Theme.Message(this, Tone.Warning, ex.Message, "Retirar consentimiento");
                Reload();
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Retirar consentimiento (base de datos)");
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Retirar consentimiento");
                return;
            }
            Reload();
            Theme.Message(this, Tone.Success,
                "Consentimiento retirado. Se borró el teléfono y se anonimizó el DNI en " + result.ReceiptsAnonymized + " recibo(s) y " + result.TicketsAnonymized + " ticket(s)." +
                (result.ReceiptsKept > 0 ? " " + result.ReceiptsKept + " recibo(s) mayores a " + Theme.Money(AppData.DniObligatorioDesde) + " conservan el documento (SUNAT)." : ""),
                "Retirar consentimiento");
        }
    }
}
