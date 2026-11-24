using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EdenPOS
{
    public sealed class ShiftView : UserControl, IRefreshable
    {
        private readonly Panel body;
        private ScreenHeader header;
        // Resultado del último cierre de turno (se muestra como Banner en el estado vacío).
        private string lastClosureText;
        private Tone lastClosureTone = Tone.Info;

        public ShiftView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Canvas;
            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(Theme.Space.Xxl, Theme.Space.Xl, Theme.Space.Xxl, Theme.Space.Xxl), AutoScroll = true };
            Controls.Add(body);
            RefreshView();
        }

        public void RefreshView()
        {
            if (body == null) return;
            body.SuspendLayout();
            try
            {
                foreach (var control in body.Controls.Cast<Control>().ToArray()) control.Dispose();
                body.Controls.Clear();
                var shift = AppData.OpenShift;
                BuildHeader(shift);
                if (shift == null) RenderClosed();
                else RenderOpen(shift);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Turno (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Turno");
            }
            finally
            {
                body.ResumeLayout();
            }
        }

        // La cabecera se recrea en cada refresco porque sus acciones dependen de si hay turno abierto.
        private void BuildHeader(Shift shift)
        {
            if (header != null) { Controls.Remove(header); header.Dispose(); }
            header = new ScreenHeader { Title = "Turno" };
            if (shift == null)
            {
                header.Subtitle = "Abra la caja para cobrar ventas y registrar movimientos de caja.";
                var closedPill = Theme.Pill("Cerrado", Tone.Neutral, true);
                header.AddAction(closedPill);
                closedPill.Margin = new Padding(Theme.Space.Sm, (Theme.Dim.Control - Theme.Dim.Pill) / 2, 0, 0);
            }
            else
            {
                header.Subtitle = "Cierre de caja n.º " + shift.Number + "  ·  Abierto por " + shift.Employee + " el " + shift.OpenedAt.ToString("dd/MM/yyyy HH:mm");
                var close = Theme.Button("Cerrar turno", ButtonVariant.Primary);
                close.Click += (s, e) => CloseShift(shift);
                var movement = Theme.Button("Registrar movimiento", ButtonVariant.Secondary);
                movement.Click += (s, e) => { using (var dialog = new TreasuryDialog(shift)) dialog.ShowDialog(FindForm()); RefreshViewDeferred(); };
                header.AddAction(close);
                header.AddAction(movement);
                var openPill = Theme.Pill("Abierto", Tone.Success, true);
                header.AddAction(openPill);
                openPill.Margin = new Padding(Theme.Space.Sm, (Theme.Dim.Control - Theme.Dim.Pill) / 2, Theme.Space.Md, 0);
            }
            Controls.Add(header);   // añadido el último: se acopla primero y queda arriba; body (índice 0) ocupa el resto
        }

        private void RenderClosed()
        {
            var empty = Theme.EmptyState("No hay un turno abierto", "Abra la caja con el fondo inicial: sin un turno abierto no se pueden cobrar ventas ni registrar movimientos de caja.", "Abrir turno");
            empty.Action.Variant = ButtonVariant.Primary;
            empty.Action.Click += OpenShift_Click;
            if (!string.IsNullOrEmpty(lastClosureText))
            {
                var banner = Theme.Banner(lastClosureText, lastClosureTone);
                banner.Dock = DockStyle.Top;
                body.Controls.Add(banner);
            }
            body.Controls.Add(empty);
            empty.BringToFront();
        }

        private void RenderOpen(Shift shift)
        {
            lastClosureText = null;
            // RF-05: un solo cálculo por medio (el mismo que usa el cierre) para el esperado de efectivo, el de Yape y la proyección.
            var balance = AppData.GetShiftBalance(shift);
            var movements = AppData.CashMovements.Where(x => x.ShiftId == shift.Id).OrderByDescending(x => x.CreatedAt).ToList();

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Canvas, Margin = new Padding(0), Padding = new Padding(0), MinimumSize = new Size(640, 580) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 312f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Cifra principal: efectivo esperado en el cajón y, debajo, la proyección del disponible (efectivo + Yape).
            var hero = new CardPanel { Dock = DockStyle.Fill, Elevated = true, Padding = new Padding(Theme.Space.Xl), Margin = new Padding(0, 0, Theme.Space.Xl, Theme.Space.Xl) };
            var heroLabel = Theme.LabelField("Efectivo esperado en caja");
            heroLabel.Dock = DockStyle.Top; heroLabel.AutoSize = false; heroLabel.Height = 24;
            var heroValue = new Label { Text = Theme.Money(balance.ExpectedCash), Font = Theme.Display, ForeColor = Theme.Ink, Dock = DockStyle.Top, Height = 56, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            var projectionLabel = Theme.LabelField("Proyección del disponible (efectivo + Yape)");
            projectionLabel.Dock = DockStyle.Top; projectionLabel.AutoSize = false; projectionLabel.Height = 32; projectionLabel.TextAlign = ContentAlignment.BottomLeft;
            var projectionValue = new Label { Text = Theme.Money(balance.ProjectedAvailable), Font = Theme.Heading, ForeColor = Theme.GreenDark, Dock = DockStyle.Top, Height = 40, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            var heroNote = new Label { Text = "El Yape no entra al cajón: al cerrar se confirma con lo que muestra la app Yape.", Font = Theme.Caption, ForeColor = Theme.Muted, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.BottomLeft };
            // El último añadido se acopla primero; el que rellena va al frente.
            hero.Controls.Add(heroNote);
            hero.Controls.Add(projectionValue);
            hero.Controls.Add(projectionLabel);
            hero.Controls.Add(heroValue);
            hero.Controls.Add(heroLabel);
            heroNote.BringToFront();
            table.Controls.Add(hero, 0, 0);

            // Desglose alineado por medio de pago.
            var breakdown = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, Theme.Space.Lg, Theme.Space.Xl, Theme.Space.Lg), Margin = new Padding(0, 0, 0, Theme.Space.Xl) };
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
            var title = new Label { Text = "Desglose", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            rows.Controls.Add(title, 0, 0); rows.SetColumnSpan(title, 2);
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            AddBreakdown(rows, 1, "Apertura de caja", Theme.Money(shift.OpeningAmount), Theme.Ink);
            AddBreakdown(rows, 2, "Cobros en efectivo (ventas y saldos)", Theme.Money(balance.CashSales), Theme.Ink);
            AddBreakdown(rows, 3, "Movimientos en efectivo (neto)", Theme.Money(balance.CashMovements), balance.CashMovements < 0 ? Theme.Danger : Theme.Ink);
            AddBreakdown(rows, 4, "Cobros por Yape (ventas y saldos)", Theme.Money(balance.YapeSales), Theme.Ink);
            AddBreakdown(rows, 5, "Movimientos por Yape (neto)", Theme.Money(balance.YapeMovements), balance.YapeMovements < 0 ? Theme.Danger : Theme.Ink);
            AddBreakdown(rows, 6, "Yape esperado (no suma al efectivo)", Theme.Money(balance.ExpectedYape), Theme.Ink, true);
            AddBreakdown(rows, 7, "Disponible proyectado", Theme.Money(balance.ProjectedAvailable), Theme.GreenDark, true);
            breakdown.Controls.Add(rows);
            table.Controls.Add(breakdown, 1, 0);

            // Movimientos de caja.
            var list = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Xl, Theme.Space.Lg, Theme.Space.Xl, Theme.Space.Lg), Margin = new Padding(0) };
            var listTitle = new Label { Text = "Movimientos de caja", Font = Theme.Title, ForeColor = Theme.Ink, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            var listNet = new Label
            {
                Text = movements.Count == 0 ? "" : "Neto: efectivo " + Theme.Money(balance.CashMovements) + "  ·  Yape " + Theme.Money(balance.YapeMovements),
                Font = Theme.Subhead, ForeColor = Theme.Muted, Dock = DockStyle.Right, Width = 380, AutoSize = false, TextAlign = ContentAlignment.MiddleRight
            };
            var listHead = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent };
            listHead.Controls.Add(listTitle);
            listHead.Controls.Add(listNet);
            if (movements.Count == 0)
            {
                var none = Theme.EmptyState("Aún no hay movimientos de caja", "Los depósitos y salidas en efectivo o por Yape que registre aparecerán aquí.");
                list.Controls.Add(none);
            }
            else
            {
                var grid = new DataGridView { Dock = DockStyle.Fill };
                Theme.StyleGrid(grid);
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Hora", Width = 80, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Tipo", Width = 110, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Medium", HeaderText = "Medio", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Comment", HeaderText = "Empleado y comentario", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Amount", HeaderText = "Importe", Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
                foreach (var item in movements)
                {
                    var row = grid.Rows.Add(item.CreatedAt.ToString("HH:mm"), item.Amount < 0 ? "Salida" : "Depósito", item.Medium, item.Employee + " - " + item.Comment, Theme.Money(item.Amount));
                    if (item.Amount < 0) grid.Rows[row].Cells["Amount"].Style.ForeColor = Theme.Danger;
                }
                grid.ClearSelection();
                list.Controls.Add(grid);
            }
            list.Controls.Add(listHead);
            table.Controls.Add(list, 0, 1); table.SetColumnSpan(list, 2);
            body.Controls.Add(table);
        }

        private static void AddBreakdown(TableLayoutPanel rows, int row, string label, string value, Color valueColor, bool emphasis = false)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7f));
            var name = new Label { Text = label, Font = emphasis ? Theme.Subhead : Theme.Body, ForeColor = Theme.Ink, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            var amount = new Label { Text = value, Font = Theme.Subhead, ForeColor = valueColor, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
            rows.Controls.Add(name, 0, row);
            rows.Controls.Add(amount, 1, row);
        }

        private static string Capitalize(string text)
        {
            return string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0]) + text.Substring(1);
        }

        private void OpenShift_Click(object sender, EventArgs e)
        {
            decimal amount;
            if (!PromptDialog.AskMoney(FindForm(), "Abrir turno", "Fondo inicial de caja", out amount)) return;
            try
            {
                AppData.OpenNewShift(amount);
            }
            catch (InvalidOperationException ex)
            {
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "Abrir turno");
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Abrir turno (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Abrir turno");
            }
            RefreshViewDeferred();
        }

        // Los manejadores de Click de botones que RefreshView destruye no pueden refrescar en línea: se difiere al siguiente ciclo de mensajes.
        private void RefreshViewDeferred()
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(new Action(() => { if (!IsDisposed) RefreshView(); }));
        }

        private void CloseShift(Shift shift)
        {
            ShiftBalance balance;
            try
            {
                balance = AppData.GetShiftBalance(shift);
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Cerrar turno: calcular esperado (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Cerrar turno");
                return;
            }
            var expected = balance.ExpectedCash;
            var expectedYape = balance.ExpectedYape;
            // RF-05: cierre por medio de pago. Se cuenta el efectivo del cajón y se confirma el Yape con lo que muestra la app Yape.
            decimal counted, countedYape;
            if (!PromptDialog.AskMoney(FindForm(), "Cerrar turno", "Efectivo contado (esperado " + Theme.Money(expected) + ")", out counted, expected)) return;
            if (!PromptDialog.AskMoney(FindForm(), "Cerrar turno", "Yape confirmado en la app (esperado " + Theme.Money(expectedYape) + ")", out countedYape, expectedYape)) return;
            var difference = counted - expected;
            var yapeDifference = countedYape - expectedYape;
            var confirm = MessageBox.Show(
                "Efectivo esperado: " + Theme.Money(expected) + "\n" +
                "Efectivo contado: " + Theme.Money(counted) + "\n" +
                "Diferencia de efectivo: " + Theme.Money(difference) + "\n\n" +
                "Yape esperado: " + Theme.Money(expectedYape) + "\n" +
                "Yape confirmado: " + Theme.Money(countedYape) + "\n" +
                "Diferencia de Yape: " + Theme.Money(yapeDifference) + "\n\n¿Cerrar el turno?",
                "Resumen de cierre",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            // RN-09: con diferencia (faltante o sobrante) en cualquiera de los dos medios la observación es obligatoria y queda guardada en el turno.
            string observation = null;
            if (difference != 0m || yapeDifference != 0m)
            {
                var label = Capitalize(AppData.DescribeClosingDifference(difference, yapeDifference)) + ". Explique el motivo";
                while (true)
                {
                    if (!PromptDialog.AskText(FindForm(), "Observación del cierre", label, out observation, observation ?? "")) return;
                    if (Validation.IsValidRequiredText(observation, 3, 200)) break;
                    Theme.Message(FindForm(), Tone.Warning, "Ingrese una observación de 3 a 200 caracteres.", "Observación del cierre");
                }
            }
            Shift closed;
            try
            {
                closed = AppData.CloseShift(shift.Id, counted, countedYape, observation);
            }
            catch (InvalidOperationException ex)
            {
                // Otra caja cambió el esperado mientras se contaba: el turno sigue abierto.
                Theme.Message(FindForm(), Tone.Warning, ex.Message, "Cerrar turno");
                RefreshViewDeferred();
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Cerrar turno (base de datos)");
                Theme.Message(FindForm(), Tone.Danger, AppData.DescribeDatabaseError(ex), "Cerrar turno");
                return;
            }
            if (closed == null)
            {
                Theme.Message(FindForm(), Tone.Info, "Este turno ya se cerró en otra caja.", "Cerrar turno");
                RefreshViewDeferred();
                return;
            }
            // Montos guardados (calculados dentro de la transacción del cierre). Color semántico: cuadra = éxito, algún faltante = error, solo sobrante = advertencia.
            expected = closed.ExpectedAmount;
            expectedYape = closed.ExpectedYape ?? expectedYape;
            difference = closed.ClosingAmount - expected;
            yapeDifference = (closed.CountedYape ?? countedYape) - expectedYape;
            var squared = difference == 0m && yapeDifference == 0m;
            lastClosureTone = squared ? Tone.Success : difference < 0m || yapeDifference < 0m ? Tone.Danger : Tone.Warning;
            lastClosureText = "Turno cerrado. Efectivo: esperado " + Theme.Money(expected) + ", contado " + Theme.Money(closed.ClosingAmount) +
                ". Yape: esperado " + Theme.Money(expectedYape) + ", confirmado " + Theme.Money(closed.CountedYape ?? countedYape) + ". " +
                (squared ? "La caja cuadra, sin diferencia." : Capitalize(AppData.DescribeClosingDifference(difference, yapeDifference)) + ".");
            RefreshViewDeferred();
            // Copia automática en segundo plano (solo si está activada). Sin ventanas: si termina mientras se ve el aviso de cierre, añade una línea.
            var closureText = lastClosureText;
            AppData.RunAutoBackupAsync(false, result =>
            {
                if (!result.Ran || !IsHandleCreated || IsDisposed) return;
                var note = result.Ok ? "Copia automática guardada." : "No se pudo guardar la copia automática: " + result.Message;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || lastClosureText != closureText) return; // otro turno u otro aviso: no tocar
                        lastClosureText = closureText + " " + note;
                        RefreshView();
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }
    }

    public sealed class TreasuryDialog : Form
    {
        private readonly Shift shift;
        private readonly SegmentChip cashChip, yapeChip;
        private string medium = CashMedia.Efectivo;
        private readonly NumericUpDown amount;
        private readonly TextBox comment;
        private readonly DataGridView grid;
        private readonly Label balance;
        private readonly Panel listHost;
        private EmptyState noMovements;

        public TreasuryDialog(Shift currentShift)
        {
            shift = currentShift;
            Text = "Gestión de tesorería";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 740);
            MinimumSize = new Size(760, 700);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Canvas;
            Font = Theme.Body;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(Theme.Space.Xl), BackColor = Theme.Canvas };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 326f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            // Título y contexto.
            var head = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            var title = new Label { Text = "Registrar movimiento", Font = Theme.Title, ForeColor = Theme.Ink, Dock = DockStyle.Top, Height = 32, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            var context = new Label { Text = "Cierre de caja n.º " + shift.Number + "  ·  Depósitos y salidas del turno, en efectivo o por Yape", Font = Theme.Body, ForeColor = Theme.Muted, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft };
            head.Controls.Add(context);
            head.Controls.Add(title);
            root.Controls.Add(head, 0, 0);

            // Formulario.
            var entry = new CardPanel { Dock = DockStyle.Fill, Elevated = true, Padding = new Padding(Theme.Space.Xl, Theme.Space.Lg, Theme.Space.Xl, Theme.Space.Lg), Margin = new Padding(0, 0, 0, Theme.Space.Md) };
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            // RF-04: medio del movimiento. Solo los de Efectivo cambian el efectivo esperado en el cajón.
            var mediumLabel = Theme.LabelField("Medio");
            var mediumRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            cashChip = MediumChip(CashMedia.Efectivo);
            yapeChip = MediumChip(CashMedia.Yape);
            mediumRow.Controls.Add(cashChip);
            mediumRow.Controls.Add(yapeChip);
            SelectMedium(CashMedia.Efectivo);
            var amountLabel = Theme.LabelField("Cantidad");
            amount = new NumericUpDown { Font = Theme.Subhead, DecimalPlaces = 2, Maximum = 1000000, Minimum = 0, ThousandsSeparator = true };
            var amountField = Theme.Field(amount, Theme.Dim.Field);
            amountField.Dock = DockStyle.Fill;
            var commentLabel = Theme.LabelField("Comentario");
            comment = new TextBox { Font = Theme.Body, Tag = "Motivo del movimiento (3 a 160 caracteres)" };
            var commentField = Theme.Field(comment, Theme.Dim.Field);
            commentField.Dock = DockStyle.Fill;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            var deposit = Theme.Button("Registrar depósito", ButtonVariant.Primary);
            deposit.Width = 190; deposit.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            deposit.Click += (s, e) => SaveMovement("Depósito");
            var withdrawal = Theme.Button("Registrar salida", ButtonVariant.Secondary);
            withdrawal.Width = 170; withdrawal.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            withdrawal.Click += (s, e) => SaveMovement("Salida");
            buttons.Controls.Add(deposit);
            buttons.Controls.Add(withdrawal);
            form.Controls.Add(mediumLabel, 0, 0);
            form.Controls.Add(mediumRow, 0, 1);
            form.Controls.Add(amountLabel, 0, 2);
            form.Controls.Add(amountField, 0, 3);
            form.Controls.Add(commentLabel, 0, 4);
            form.Controls.Add(commentField, 0, 5);
            form.Controls.Add(buttons, 0, 6);
            entry.Controls.Add(form);
            root.Controls.Add(entry, 0, 1);

            // Encabezado de la lista con el neto.
            var listHeader = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            var listTitle = new Label { Text = "Movimientos del turno", Font = Theme.Subhead, ForeColor = Theme.Ink, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            balance = new Label { Dock = DockStyle.Right, Width = 420, Font = Theme.Subhead, ForeColor = Theme.Ink, AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
            listHeader.Controls.Add(listTitle);
            listHeader.Controls.Add(balance);
            root.Controls.Add(listHeader, 0, 2);

            listHost = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space.Sm), Margin = new Padding(0) };
            grid = new DataGridView { Dock = DockStyle.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Hora", Width = 80, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Medium", HeaderText = "Medio", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Comment", HeaderText = "Empleado y comentario", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Amount", HeaderText = "Importe", Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            listHost.Controls.Add(grid);
            root.Controls.Add(listHost, 0, 3);
            LoadMovements();
        }

        private SegmentChip MediumChip(string text)
        {
            var chip = new SegmentChip(text) { Tag = text, ShowCheck = true, CornerRadius = Theme.Radius.Control, Height = Theme.Dim.Control, Margin = new Padding(0, 0, Theme.Space.Sm, 0) };
            chip.Size = chip.GetPreferredSize(Size.Empty);
            chip.Width = Math.Max(chip.Width, 130);
            chip.Height = Theme.Dim.Control;
            chip.Click += (s, e) => SelectMedium((string)((Control)s).Tag);
            return chip;
        }

        private void SelectMedium(string value)
        {
            medium = value;
            cashChip.Active = value == CashMedia.Efectivo;
            yapeChip.Active = value == CashMedia.Yape;
        }

        private void SaveMovement(string type)
        {
            if (amount.Value <= 0)
            {
                Theme.Message(this, Tone.Warning, "Ingrese una cantidad mayor que cero.", "Tesorería");
                return;
            }
            if (!Validation.IsValidRequiredText(comment.Text, 3, 160))
            {
                Theme.Message(this, Tone.Warning, "Ingrese un comentario de 3 a 160 caracteres para justificar el movimiento.", "Tesorería");
                comment.Focus();
                return;
            }
            try
            {
                AppData.AddCashMovement(type, amount.Value, comment.Text, medium);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Theme.Message(this, Tone.Warning, ex.Message, "Tesorería");
                return;
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Movimiento de tesorería (base de datos)");
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Tesorería");
                return;
            }
            amount.Value = 0;
            comment.Clear();
            LoadMovements();
        }

        private void LoadMovements()
        {
            System.Collections.Generic.List<CashMovement> movements;
            try
            {
                movements = AppData.CashMovements.Where(x => x.ShiftId == shift.Id).OrderByDescending(x => x.CreatedAt).ToList();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                AppData.LogError(ex, "Listar movimientos de tesorería (base de datos)");
                Theme.Message(this, Tone.Danger, AppData.DescribeDatabaseError(ex), "Tesorería");
                return;
            }
            grid.Rows.Clear();
            foreach (var item in movements)
            {
                var row = grid.Rows.Add(item.CreatedAt.ToString("HH:mm"), item.Medium, item.Employee + " - " + item.Comment, Theme.Money(item.Amount));
                if (item.Amount < 0) grid.Rows[row].Cells["Amount"].Style.ForeColor = Theme.Danger;
            }
            grid.ClearSelection();
            grid.Visible = movements.Count > 0;
            if (movements.Count == 0 && noMovements == null)
            {
                noMovements = Theme.EmptyState("Aún no hay movimientos", "Registre un depósito o una salida con el formulario de arriba.");
                listHost.Controls.Add(noMovements);
            }
            if (noMovements != null) noMovements.Visible = movements.Count == 0;
            var cashNet = movements.Where(x => x.Medium != CashMedia.Yape).Sum(x => x.Amount);
            var yapeNet = movements.Where(x => x.Medium == CashMedia.Yape).Sum(x => x.Amount);
            balance.Text = "Neto: efectivo " + Theme.Money(cashNet) + "  ·  Yape " + Theme.Money(yapeNet);
            balance.ForeColor = cashNet < 0 || yapeNet < 0 ? Theme.Danger : Theme.Ink;
        }
    }
}
