using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;

namespace EdenPOS
{
    // =====================================================================================================================
    // SISTEMA DE DISEÑO DE EDENPOS  (principios de las guías de interfaz de Apple aplicables a WinForms/GDI+)
    //
    // Regla de oro: NINGUN color, fuente, espacio ni radio sale "de la nada": todo viene de un token de este archivo o de
    // Controls.cs. Si falta un token, se añade aquí; no se inventa en la pantalla.
    //
    // MATERIALES (jerarquia plana, sin blur):  Sidebar* (barra lateral, el material mas pesado)  >  Canvas (lienzo claro)
    //   >  Surface/tarjeta blanca con borde 1 px (CardPanel).  Nunca apilar superficies claras sobre superficies claras:
    //   una tarjeta va sobre Canvas, y dentro de la tarjeta se separa con Divider, no con otra tarjeta.
    //
    // COLOR (contraste calculado WCAG; texto >= 4.5:1 en su fondo):
    //   Primary (22,128,54): relleno de boton primario y texto verde. Blanco sobre el = 5.03 · sobre Canvas 4.76 · sobre GreenSoft 4.54
    //   PrimaryHover (18,112,47) blanco 6.20 · GreenDark (12,92,38) blanco 8.16, sobre GreenSoft 7.36: estado "seleccionado"/enfasis oscuro
    //   (distinto de Primary a proposito: p. ej. candado elegido vs libre) · GreenPressed (9,74,30) blanco 10.45
    //   Green (37,174,74) = verde de MARCA: solo para relleno sin texto (acentos, barras, iconos); blanco sobre el = 2.90 (NO usar para texto).
    //   Ink (texto principal) sobre blanco 14.1 · Muted (secundario, 100,108,116) 5.33 blanco / 5.05 Canvas · TextTertiary 4.94 / 4.68
    //   Semanticos (texto fuerte sobre su fondo suave): Success 4.54 · Warning 6.26 · Danger 5.84 · Info 6.17
    //   Texto sobre SidebarBack: SidebarText 11.46 · SidebarMuted 7.32 · blanco sobre SidebarActive 8.36
    //   Deshabilitado (TextDisabled) esta exento de contraste, pero se ve claramente "apagado".
    //
    // ESPACIO (escala fija, en px):  Theme.Space.Xs 4 · Sm 8 · Md 12 · Lg 16 · Xl 24 · Xxl 32 · Xxxl 48
    //   Margen de pantalla = Xxl; separacion entre tarjetas = Xl; interior de tarjeta = Xl; entre controles de un grupo = Sm/Md.
    // RADIO:  Theme.Radius.Control 6 (botones, campos) · Card 12 (tarjetas, dialogos) · Pill 999 (insignias)
    // ALTURAS:  Theme.Dim.Control 44 (botones) · Field 40 (campos) · GridRow 48 · GridHeader 40 · Pill 24
    //
    // TIPOGRAFIA (Segoe UI; jerarquia = tamaño + peso juntos; GDI+ no tiene tracking):
    //   Display 26 semibold (cifra hero) · Heading 18 semibold (titulo de pantalla) · Title 14 semibold (titulo de tarjeta/dialogo)
    //   Subhead 11 semibold (subtitulo de seccion) · Emphasis 12 semibold (importes y totales) · Body 10.5 (texto y celdas)
    //   FieldLabel 9.5 semibold (etiqueta de campo) · Caption 9 (notas y ayudas) · Mono (Consolas 10, series/codigos)
    //   ButtonFont 10 semibold. Para texto suelto: Theme.Label(...), Theme.LabelField(...).
    //
    // QUE USAR EN CADA CASO
    //   Boton ............ Theme.Button("Registrar entrada", ButtonVariant.Primary|Secondary|Destructive|Ghost)  (o Button(text, bool primary)).
    //                      UNA sola accion Primary por pantalla/dialogo. Destructive solo para eliminar/reembolsar. Ghost para acciones terciarias.
    //                      Cambiar BackColor/ForeColor/FlatAppearance.BorderColor despues de crearlo SIGUE FUNCIONANDO (ver EdenButton),
    //                      pero para estados (activo/inactivo, ocupado) prefiera variantes o BackColor con tokens (GreenSoft, Primary, Danger...).
    //                      OJO: BackColor = Theme.Green (marca) con texto blanco NO cumple contraste (2.90); use Theme.Primary. Sin ForeColor, el
    //                      boton elige solo blanco o Ink segun contraste.
    //   Campo de texto ... Theme.Field(textBox)  -> contenedor redondeado que se resalta en verde con foco (idem NumericUpDown, DateTimePicker).
    //   Lista desplegable  new EdenComboBox() / Theme.Combo()  (dibujado propio, sin el gris de Windows).
    //   Tabla ............ Theme.StyleGrid(grid)  (sin lineas verticales, separador suave, cabecera semibold sin azul, seleccion verde suave).
    //   Tarjeta .......... new CardPanel()  (borde 1 px, radio 12; Elevated = sombra sutil inferior).
    //   Cabecera pantalla  new ScreenHeader { Title, Subtitle } + header.AddAction(boton)  (acciones a la derecha; la primaria se añade primero).
    //   Estado ........... Theme.Pill("Aceptado", Tone.Success)   Tone: Neutral · Brand · Info · Success · Warning · Danger.
    //   Feedback ......... estado → Pill / Banner; completado → Banner Success o Theme.Message(...Success); advertencia → Banner Warning;
    //                      error → Banner Danger / MessageBox de error. Validar en linea, no al enviar.
    //   Vacio ............ Theme.EmptyState("Todavía no hay recibos", "Cuando cobre una venta, aparecerá aquí.")
    //   Separador ........ Theme.Separator()  (1 px, color Divider; vertical: Theme.Separator(true)).
    //   Etiquetas ........ Mayuscula inicial y especificas ("Registrar entrada", "Guardar cambios"); los nombres de producto van tal cual.
    //
    // MOVIMIENTO: no hay animaciones. Respuesta inmediata al presionar (EdenButton repinta en MouseDown). Si alguna pantalla añade una
    // transicion minima, debe consultar Theme.UseMotion (= SystemInformation.UIEffectsEnabled) y no animar cuando sea false.
    // =====================================================================================================================
    public static class Theme
    {
        // ---- Marca (escala verde) -------------------------------------------------------------------------------------
        public static readonly Color Green = Color.FromArgb(37, 174, 74);          // marca; solo relleno sin texto
        public static readonly Color Lime = Color.FromArgb(104, 184, 37);          // acento de marca (detalles)
        public static readonly Color Primary = Color.FromArgb(22, 128, 54);        // accion principal y texto verde (blanco 5.03)
        public static readonly Color GreenDark = Color.FromArgb(12, 92, 38);      // verde profundo: seleccionado, enfasis, texto fuerte (blanco 8.16)
        public static readonly Color PrimaryHover = Color.FromArgb(18, 112, 47);   // hover del boton primario (blanco 6.20)
        public static readonly Color GreenPressed = Color.FromArgb(9, 74, 30);     // presionado del primario (blanco 10.45)
        public static readonly Color GreenSoft = Color.FromArgb(232, 247, 236);    // fondo suave: seleccion, hover, activo
        public static readonly Color SoftPressed = Color.FromArgb(208, 235, 217);  // GreenSoft presionado

        // ---- Neutros ----------------------------------------------------------------------------------------------------
        public static readonly Color Canvas = Color.FromArgb(247, 249, 250);       // lienzo de pantalla
        public static readonly Color Surface = Color.White;                        // tarjetas y campos
        public static readonly Color SurfaceSunken = Color.FromArgb(241, 244, 245);// fondo hundido: hover de fila/ghost, pill neutra
        public static readonly Color Border = Color.FromArgb(224, 228, 232);       // borde de tarjeta y de cabecera de tabla
        public static readonly Color BorderStrong = Color.FromArgb(201, 208, 214); // borde de campos y de botones secundarios
        public static readonly Color Divider = Color.FromArgb(236, 240, 242);      // separador suave (filas, secciones)
        public static readonly Color Ink = Color.FromArgb(37, 44, 52);             // texto principal
        public static readonly Color Muted = Color.FromArgb(100, 108, 116);        // texto secundario
        public static readonly Color TextTertiary = Color.FromArgb(106, 113, 121); // ayudas, marcas de agua legibles
        public static readonly Color TextDisabled = Color.FromArgb(140, 148, 155); // solo deshabilitado
        public static readonly Color DisabledFill = Color.FromArgb(236, 239, 241); // relleno de boton deshabilitado
        public static readonly Color TextPrimary = Ink;
        public static readonly Color TextSecondary = Muted;

        // ---- Barra lateral (el material mas pesado) ---------------------------------------------------------------------
        public static readonly Color SidebarBack = Color.FromArgb(24, 52, 38);
        public static readonly Color SidebarHover = Color.FromArgb(34, 68, 50);
        public static readonly Color SidebarActive = Color.FromArgb(44, 86, 64);   // blanco encima: 8.36
        public static readonly Color SidebarText = Color.FromArgb(226, 240, 231);  // 11.46 sobre SidebarBack
        public static readonly Color SidebarMuted = Color.FromArgb(168, 198, 180); // 7.32

        // ---- Semanticos: color fuerte (texto/relleno con blanco) y version suave de fondo --------------------------------
        public static readonly Color Success = Color.FromArgb(22, 128, 54);        public static readonly Color SuccessSoft = Color.FromArgb(232, 247, 236);   // 4.54
        public static readonly Color Warning = Color.FromArgb(133, 77, 0);         public static readonly Color WarningSoft = Color.FromArgb(255, 244, 214);   // 6.26
        public static readonly Color Danger = Color.FromArgb(176, 38, 32);         public static readonly Color DangerSoft = Color.FromArgb(253, 236, 234);    // 5.84
        public static readonly Color Info = Color.FromArgb(22, 90, 160);           public static readonly Color InfoSoft = Color.FromArgb(232, 242, 252);      // 6.17
        public static readonly Color DangerHover = Color.FromArgb(150, 30, 25);    // boton destructivo: hover (blanco 8.41)
        public static readonly Color DangerPressed = Color.FromArgb(125, 24, 20);  // boton destructivo: presionado

        // ---- Escalas ----------------------------------------------------------------------------------------------------
        public static class Space { public const int Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 24, Xxl = 32, Xxxl = 48; }
        public static class Radius { public const int Control = 6, Card = 12, Pill = 999; }
        public static class Dim { public const int Control = 44, Field = 40, GridRow = 48, GridHeader = 40, Pill = 24; }

        // Movimiento minimo y opcional: equivalente a "reducir movimiento" de Windows.
        public static bool UseMotion { get { return SystemInformation.UIEffectsEnabled; } }

        // ---- Tipografia (Segoe UI; semibold = familia "Segoe UI Semibold" en estilo Regular) -----------------------------
        public static readonly Font Body = new Font("Segoe UI", 10.5f, FontStyle.Regular);
        public static readonly Font Small = new Font("Segoe UI", 9f, FontStyle.Regular);
        public static readonly Font Caption = Small;
        public static readonly Font Heading = new Font("Segoe UI Semibold", 18f, FontStyle.Regular);
        public static readonly Font Display = new Font("Segoe UI Semibold", 26f, FontStyle.Regular);
        public static readonly Font Title = new Font("Segoe UI Semibold", 14f, FontStyle.Regular);
        public static readonly Font Subhead = new Font("Segoe UI Semibold", 11f, FontStyle.Regular);
        public static readonly Font Emphasis = new Font("Segoe UI Semibold", 12f, FontStyle.Regular);
        public static readonly Font FieldLabel = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular);
        public static readonly Font ButtonFont = new Font("Segoe UI Semibold", 10f, FontStyle.Regular);
        public static readonly Font Mono = new Font("Consolas", 10f, FontStyle.Regular);

        // Fuente Segoe UI de cualquier tamaño: negrita = Semibold (nunca Bold sintetico).
        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            var bold = (style & FontStyle.Bold) != 0;
            return new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size, style & ~FontStyle.Bold);
        }

        // ---- Fabricas de controles ----------------------------------------------------------------------------------------
        public static EdenButton Button(string text, bool primary = false)
        {
            return Button(text, primary ? ButtonVariant.Primary : ButtonVariant.Secondary);
        }

        public static EdenButton Button(string text, ButtonVariant variant)
        {
            return new EdenButton
            {
                Text = text,
                Variant = variant,
                Height = Dim.Control,
                Margin = new Padding(6),
                Padding = new Padding(Space.Md, 0, Space.Md, 0),
                AutoSize = false
            };
        }

        public static Label Label(string text, float size = 10.5f, FontStyle style = FontStyle.Regular, Color? color = null)
        {
            return new Label
            {
                Text = text,
                Font = Font(size, style),
                ForeColor = color ?? Ink,
                AutoSize = true,
                Margin = new Padding(0)
            };
        }

        // Etiqueta de campo ("Importe recibido"): semibold 9.5 en texto secundario.
        public static Label LabelField(string text)
        {
            return new Label { Text = text, Font = FieldLabel, ForeColor = Muted, AutoSize = true, Margin = new Padding(0) };
        }

        // TextBox simple (sin contenedor). Para el aspecto completo del sistema use Theme.Field(Theme.TextBox("Buscar")).
        public static TextBox TextBox(string placeholder = "")
        {
            var box = new TextBox
            {
                Font = Body,
                BorderStyle = BorderStyle.FixedSingle,
                Height = 36,
                Tag = placeholder
            };
            return box;
        }

        // Envuelve un TextBox / NumericUpDown / DateTimePicker / ComboBox en un contenedor redondeado que se resalta con foco.
        // El contenedor hereda Bounds/Dock/Anchor/Margin del control; si el control ya estaba en un padre, lo reemplaza en su sitio.
        // Para mostrar/ocultar el campo use el CONTENEDOR (field.Visible), no el control interior.
        public static EdenField Field(Control inner, int height = 0) { return EdenField.Wrap(inner, height); }

        public static EdenComboBox Combo()
        {
            return new EdenComboBox();
        }

        public static Pill Pill(string text, Tone tone = Tone.Neutral, bool dot = false)
        {
            return new Pill { Text = text, Tone = tone, ShowDot = dot };
        }

        public static Banner Banner(string text, Tone tone)
        {
            return new Banner { Text = text, Tone = tone };
        }

        public static EmptyState EmptyState(string title, string hint = "", string actionText = null)
        {
            return new EmptyState { Title = title, Hint = hint, ActionText = actionText };
        }

        public static Divider Separator(bool vertical = false)
        {
            return new Divider { Vertical = vertical };
        }

        // Cuadro de mensaje por tipo de feedback (completado, advertencia, error, estado).
        public static DialogResult Message(IWin32Window owner, Tone tone, string text, string title = "Edén POS")
        {
            var icon = tone == Tone.Danger ? MessageBoxIcon.Error : tone == Tone.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
            return MessageBox.Show(owner, text, title, MessageBoxButtons.OK, icon);
        }

        // ---- Utilidades de color ------------------------------------------------------------------------------------------
        public static Color ParseColor(string value, Color fallback)
        {
            try { return ColorTranslator.FromHtml(value); }
            catch { return fallback; }
        }

        public static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        // Luminancia relativa WCAG (0 negro .. 1 blanco).
        public static double Luminance(Color color)
        {
            Func<int, double> channel = v => { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
        }

        public static double Contrast(Color a, Color b)
        {
            var x = Luminance(a); var y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        public static GraphicsPath RoundedPath(Rectangle bounds, int radius) { return Gfx.Round(bounds, radius); }

        // ---- Tablas -------------------------------------------------------------------------------------------------------
        public static void StyleGrid(DataGridView grid)
        {
            EnableDoubleBuffer(grid);
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;   // solo separadores horizontales
            grid.GridColor = Divider;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = Dim.GridHeader;
            grid.RowTemplate.Height = Dim.GridRow;
            grid.EnableHeadersVisualStyles = false;

            // Cabecera: texto secundario semibold; seleccion = fondo normal (sin el azul de Windows al hacer clic).
            var header = grid.ColumnHeadersDefaultCellStyle;
            header.BackColor = Surface;
            header.ForeColor = Muted;
            header.Font = FieldLabel;
            header.SelectionBackColor = Surface;
            header.SelectionForeColor = Muted;
            header.Alignment = DataGridViewContentAlignment.MiddleLeft;
            header.Padding = new Padding(Space.Sm, 0, Space.Sm, 0);
            grid.RowHeadersDefaultCellStyle.BackColor = Surface;
            grid.RowHeadersDefaultCellStyle.SelectionBackColor = Surface;

            var cell = grid.DefaultCellStyle;
            cell.BackColor = Surface;
            cell.Font = Body;
            cell.ForeColor = Ink;
            cell.SelectionBackColor = GreenSoft;     // seleccion verde suave; Ink sobre GreenSoft = 12.7:1
            cell.SelectionForeColor = Ink;

            // El DataGridView pinta TODAS las celdas con el mismo Graphics. Si un CellPainting propio deja SmoothingMode en
            // AntiAlias, los fondos de las celdas siguientes salen con bordes semitransparentes sobre el bufer (lineas oscuras
            // entre columnas). Este manejador se suscribe antes que los de cada pantalla: deja el Graphics en su estado normal
            // al empezar cada fila y cada celda. Tampoco se dibuja el rectangulo punteado de foco (la seleccion ya es visible).
            grid.RowPrePaint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.Default;
                e.PaintParts &= ~DataGridViewPaintParts.Focus;
            };

            // Linea inferior de la cabecera (Border) para separarla del cuerpo.
            grid.CellPainting += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.Default;
                if (e.RowIndex != -1 || e.ColumnIndex < 0) return;
                e.Paint(e.ClipBounds, e.PaintParts);
                using (var pen = new Pen(Border))
                    e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
                e.Handled = true;
            };

            // Aire horizontal en las celdas de texto (no en columnas de boton, casilla o imagen).
            Action<DataGridViewColumn> pad = column =>
            {
                if (column is DataGridViewButtonColumn || column is DataGridViewCheckBoxColumn || column is DataGridViewImageColumn) return;
                if (column.DefaultCellStyle.Padding == Padding.Empty)
                    column.DefaultCellStyle.Padding = new Padding(Space.Sm, 0, Space.Sm, 0);
            };
            foreach (DataGridViewColumn column in grid.Columns) pad(column);
            grid.ColumnAdded += (s, e) => pad(e.Column);
        }

        public static void EnableDoubleBuffer(Control control)
        {
            if (control == null || SystemInformation.TerminalServerSession) return;
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(control, true, null);
            }
            catch { }
        }

        public static void FitDialogToWorkingArea(Form form, int margin = 24)
        {
            if (form == null) return;
            form.Shown += (s, e) =>
            {
                var area = Screen.FromControl(form).WorkingArea;
                var maximumWidth = Math.Max(360, area.Width - margin * 2);
                var maximumHeight = Math.Max(300, area.Height - margin * 2);
                if (form.Width > maximumWidth || form.Height > maximumHeight)
                {
                    var factor = Math.Min(maximumWidth / (float)form.Width, maximumHeight / (float)form.Height);
                    form.MinimumSize = Size.Empty;
                    form.Scale(new SizeF(factor, factor));
                    form.Size = new Size(
                        Math.Min(maximumWidth, Math.Max(320, form.Width)),
                        Math.Min(maximumHeight, Math.Max(260, form.Height)));
                }
                form.Location = new Point(
                    area.Left + Math.Max(0, (area.Width - form.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - form.Height) / 2));
            };
        }

        public static string Money(decimal amount) { return AppData.Money(amount); }
    }

    // Utilidades de dibujo compartidas por los controles del sistema de diseño.
    internal static class Gfx
    {
        public static GraphicsPath Round(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var r = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
            if (r <= 0) { path.AddRectangle(bounds); return path; }
            var d = r * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Color opaco del primer ancestro (para pintar las esquinas redondeadas con el fondo real).
        public static Color ParentBack(Control control)
        {
            for (var p = control.Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return Theme.Canvas;
        }

        public static void FillParentBack(Control control, Graphics g)
        {
            using (var brush = new SolidBrush(ParentBack(control)))
                g.FillRectangle(brush, control.ClientRectangle);
        }

        public static TextFormatFlags AlignFlags(ContentAlignment alignment)
        {
            TextFormatFlags flags = 0;
            switch (alignment)
            {
                case ContentAlignment.TopLeft: case ContentAlignment.MiddleLeft: case ContentAlignment.BottomLeft: flags |= TextFormatFlags.Left; break;
                case ContentAlignment.TopRight: case ContentAlignment.MiddleRight: case ContentAlignment.BottomRight: flags |= TextFormatFlags.Right; break;
                default: flags |= TextFormatFlags.HorizontalCenter; break;
            }
            switch (alignment)
            {
                case ContentAlignment.TopLeft: case ContentAlignment.TopCenter: case ContentAlignment.TopRight: flags |= TextFormatFlags.Top; break;
                case ContentAlignment.BottomLeft: case ContentAlignment.BottomCenter: case ContentAlignment.BottomRight: flags |= TextFormatFlags.Bottom; break;
                default: flags |= TextFormatFlags.VerticalCenter; break;
            }
            return flags;
        }
    }

    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }

    // Tarjeta: superficie blanca con borde de 1 px (token Border), radio Card y, opcionalmente, una sombra sutil inferior.
    public class CardPanel : BufferedPanel
    {
        private int radius = Theme.Radius.Card;
        private bool elevated;

        public int Radius { get { return radius; } set { radius = value; Invalidate(); } }
        public Color BorderColor { get; set; } = Theme.Border;
        // true = sombra sutil de 2 px dibujada DENTRO de los limites (la cara de la tarjeta pierde 2 px abajo).
        public bool Elevated { get { return elevated; } set { elevated = value; Invalidate(); } }

        public CardPanel()
        {
            BackColor = Theme.Surface;
            Padding = new Padding(1);
            ResizeRedraw = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Gfx.FillParentBack(this, e.Graphics);   // esquinas con el color real del fondo (sin region recortada)
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var shadow = elevated ? 2 : 0;
            var face = new Rectangle(0, 0, Width - 1, Height - 1 - shadow);
            if (face.Width <= 0 || face.Height <= 0) return;
            if (shadow > 0)
            {
                using (var path = Gfx.Round(new Rectangle(0, shadow, Width - 1, Height - 1 - shadow), radius))
                using (var brush = new SolidBrush(Color.FromArgb(16, 20, 30, 40)))
                    g.FillPath(brush, path);
            }
            using (var path = Gfx.Round(face, radius))
            {
                using (var brush = new SolidBrush(BackColor)) g.FillPath(brush, path);
                using (var pen = new Pen(BorderColor)) g.DrawPath(pen, path);
            }
        }
    }

    public class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }

    public interface IRefreshable
    {
        void RefreshView();
    }
}
