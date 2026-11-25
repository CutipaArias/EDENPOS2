using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EdenPOS
{
    // Controles del sistema de diseño (ver el comentario de Theme.cs). Todo el color, espacio y radio sale de Theme.

    public enum ButtonVariant { Primary, Secondary, Destructive, Ghost }

    // Tono semantico compartido por Pill y Banner.
    public enum Tone { Neutral, Brand, Info, Success, Warning, Danger }

    internal static class Tones
    {
        public static Color Strong(Tone tone)
        {
            switch (tone)
            {
                case Tone.Brand: return Theme.Primary;
                case Tone.Info: return Theme.Info;
                case Tone.Success: return Theme.Success;
                case Tone.Warning: return Theme.Warning;
                case Tone.Danger: return Theme.Danger;
                default: return Color.FromArgb(60, 68, 76);   // neutra: 8.72 sobre SurfaceSunken
            }
        }

        public static Color Soft(Tone tone)
        {
            switch (tone)
            {
                case Tone.Brand: return Theme.GreenSoft;
                case Tone.Info: return Theme.InfoSoft;
                case Tone.Success: return Theme.SuccessSoft;
                case Tone.Warning: return Theme.WarningSoft;
                case Tone.Danger: return Theme.DangerSoft;
                default: return Theme.SurfaceSunken;
            }
        }
    }

    // =====================================================================================================================
    // EdenButton: boton redondeado dibujado con GDI+ (antialiasing). Variantes Primary / Secondary / Destructive / Ghost.
    // Estados: normal, hover, presionado (se repinta en MouseDown: respuesta inmediata), foco de teclado (anillo), deshabilitado.
    //
    // COMPATIBILIDAD con codigo que cambia el boton despues de crearlo (Theme.Button devuelve este control):
    //   * BackColor = c   -> "relleno propio": reemplaza el relleno de la variante; hover/presionado se derivan solos
    //                        (oscurece un fondo oscuro; vira hacia el verde un fondo claro). Si no se fija ForeColor, el texto se
    //                        elige solo (blanco o Ink segun contraste). BackColor = Color.Empty vuelve al relleno de la variante.
    //   * ForeColor = c   -> color de texto propio.
    //   * FlatAppearance.BorderColor / MouseOverBackColor / MouseDownBackColor -> si no estan vacios, se respetan.
    //   * Enabled = false -> aspecto deshabilitado propio (se ignoran los colores propios).
    //   * Text, Click, DialogResult, Tag, TextAlign, Padding, Font, Dock, Anchor funcionan como en Button.
    // Preferible: usar Variant en lugar de colores propios.
    // =====================================================================================================================
    public class EdenButton : Button
    {
        private ButtonVariant variant = ButtonVariant.Secondary;
        private bool hover, pressed;
        private Color? fillOverride, foreOverride;
        private readonly HoverFade hoverFade;

        public EdenButton()
        {
            hoverFade = new HoverFade(this);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Font = Theme.ButtonFont;
            Cursor = Cursors.Hand;
            TextAlign = ContentAlignment.MiddleCenter;
            Height = Theme.Dim.Control;
        }

        public ButtonVariant Variant { get { return variant; } set { variant = value; Invalidate(); } }
        public int CornerRadius { get; set; } = Theme.Radius.Control;

        public override Color BackColor
        {
            get { return fillOverride ?? VariantFill(); }
            set { fillOverride = value.IsEmpty ? (Color?)null : value; Invalidate(); }
        }

        public override Color ForeColor
        {
            get { return foreOverride ?? VariantFore(); }
            set { foreOverride = value.IsEmpty ? (Color?)null : value; Invalidate(); }
        }

        private Color VariantFill()
        {
            switch (variant)
            {
                case ButtonVariant.Primary: return Theme.Primary;
                case ButtonVariant.Destructive: return Theme.Danger;
                case ButtonVariant.Ghost: return Color.Transparent;
                default: return Theme.Surface;
            }
        }

        private Color VariantFore()
        {
            switch (variant)
            {
                case ButtonVariant.Primary: case ButtonVariant.Destructive: return Color.White;
                case ButtonVariant.Ghost: return Theme.Primary;
                default: return Theme.Ink;
            }
        }

        private static bool IsLight(Color c) { return Theme.Luminance(c) > 0.6; }

        // Hover/presionado derivados de un relleno propio.
        private static Color Shift(Color fill, int level)
        {
            return IsLight(fill)
                ? Theme.Blend(fill, Theme.Green, level == 1 ? 0.08 : 0.18)
                : Theme.Blend(fill, Color.Black, level == 1 ? 0.10 : 0.20);
        }

        private Color CurrentFill()
        {
            if (!Enabled) return variant == ButtonVariant.Ghost ? Color.Transparent : Theme.DisabledFill;
            if (pressed)
            {
                if (!FlatAppearance.MouseDownBackColor.IsEmpty) return FlatAppearance.MouseDownBackColor;
                if (fillOverride.HasValue) return Shift(fillOverride.Value, 2);
                switch (variant)
                {
                    case ButtonVariant.Primary: return Theme.GreenPressed;
                    case ButtonVariant.Destructive: return Theme.DangerPressed;
                    default: return Theme.SoftPressed;
                }
            }
            var rest = fillOverride ?? VariantFill();
            var amount = hoverFade == null ? (hover ? 1 : 0) : hoverFade.Value;
            return amount <= 0 ? rest : Theme.Blend(rest, HoverFill(), amount);   // hover: fundido breve (Motion.Fast)
        }

        private Color HoverFill()
        {
            if (!FlatAppearance.MouseOverBackColor.IsEmpty) return FlatAppearance.MouseOverBackColor;
            if (fillOverride.HasValue) return Shift(fillOverride.Value, 1);
            switch (variant)
            {
                case ButtonVariant.Primary: return Theme.PrimaryHover;
                case ButtonVariant.Destructive: return Theme.DangerHover;
                case ButtonVariant.Ghost: return Theme.SurfaceSunken;
                default: return Theme.GreenSoft;
            }
        }

        private Color CurrentBorder(Color fill)
        {
            if (!Enabled) return variant == ButtonVariant.Ghost ? Color.Transparent : (variant == ButtonVariant.Secondary ? Theme.Border : Color.Transparent);
            if (!FlatAppearance.BorderColor.IsEmpty) return FlatAppearance.BorderColor;
            if (fillOverride.HasValue) return IsLight(fillOverride.Value) ? Theme.BorderStrong : Color.Transparent;
            switch (variant)
            {
                case ButtonVariant.Secondary: return pressed ? Theme.Primary : Theme.BorderStrong;
                default: return Color.Transparent;
            }
        }

        private Color CurrentText(Color fill)
        {
            if (!Enabled) return Theme.TextDisabled;
            if (foreOverride.HasValue) return foreOverride.Value;
            if (fillOverride.HasValue)
            {
                var back = Gfx.ParentBack(this);
                var basis = fill.A == 255 ? fill : back;
                return Theme.Contrast(Color.White, basis) >= Theme.Contrast(Theme.Ink, basis) ? Color.White : Theme.Ink;
            }
            return VariantFore();
        }

        // Button (ButtonBase) activa ControlStyles.Opaque, así que Windows Forms nunca llama a OnPaintBackground en WM_PAINT.
        // Con OptimizedDoubleBuffer el búfer es compartido entre controles: si OnPaint no pinta el fondo, las esquinas (y todo
        // el botón Ghost, que no tiene relleno) muestran restos del último control pintado. El fondo se pinta en OnPaint.
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Gfx.FillParentBack(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = CurrentFill();
            var border = CurrentBorder(fill);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var path = Gfx.Round(rect, CornerRadius))
            {
                if (fill.A > 0) using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (border.A > 0) using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }

            // Foco de teclado: anillo de 2 px (Primary) con un hilo claro interior para que se vea sobre cualquier relleno.
            if (Focused && ShowFocusCues && Enabled)
            {
                using (var ring = Gfx.Round(new Rectangle(1, 1, Width - 3, Height - 3), CornerRadius))
                using (var pen = new Pen(Theme.Primary, 2f)) g.DrawPath(pen, ring);
                using (var gap = Gfx.Round(new Rectangle(3, 3, Width - 7, Height - 7), Math.Max(0, CornerRadius - 2)))
                using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1f)) g.DrawPath(pen, gap);
            }

            var textRect = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
            var flags = Gfx.AlignFlags(TextAlign) | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Text, Font, textRect, CurrentText(fill), flags);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text ?? "", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return new Size(text.Width + Padding.Horizontal + Theme.Space.Sm, Math.Max(Height, Theme.Dim.Control));
        }

        // Respuesta inmediata: se repinta en el instante de presionar, no al soltar.
        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left) { pressed = true; Invalidate(); Update(); }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            pressed = false; Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnMouseMove(MouseEventArgs mevent)
        {
            if (pressed)
            {
                var inside = ClientRectangle.Contains(mevent.Location);
                if (inside != pressed) { pressed = inside; Invalidate(); }
            }
            base.OnMouseMove(mevent);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; hoverFade.Set(true); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; hoverFade.Set(false); if (!Capture) pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = pressed = false; hoverFade.Reset(); Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnKeyDown(KeyEventArgs kevent)
        {
            if (kevent.KeyCode == Keys.Space) { pressed = true; Invalidate(); Update(); }
            base.OnKeyDown(kevent);
        }
        protected override void OnKeyUp(KeyEventArgs kevent)
        {
            if (kevent.KeyCode == Keys.Space) { pressed = false; Invalidate(); }
            base.OnKeyUp(kevent);
        }
    }

    // =====================================================================================================================
    // EdenField: contenedor redondeado (borde BorderStrong 1 px; verde 2 px con foco) para TextBox, NumericUpDown,
    // DateTimePicker o ComboBox estandar. Uso: parent.Controls.Add(Theme.Field(textBox)).
    // Hereda Bounds/Dock/Anchor/Margin del control; si el control ya estaba en un padre, lo reemplaza en su sitio.
    // Para ocultar/mostrar use field.Visible. Marca de agua: Placeholder, o el Tag del TextBox si es texto (Theme.TextBox("Buscar")).
    // No envuelva un EdenComboBox: ya dibuja su propio borde.
    // =====================================================================================================================
    public class EdenField : Panel
    {
        private const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private Control inner;
        private bool focused;
        private string placeholder;
        private bool bordered = true;

        public EdenField()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.Dim.Field;
        }

        public Control Inner { get { return inner; } }
        public int CornerRadius { get; set; } = Theme.Radius.Control;
        // false = sin marco propio (DateTimePicker: su borde lo dibuja Windows y no se puede ocultar; se deja nativo).
        public bool Bordered { get { return bordered; } set { bordered = value; Invalidate(); } }

        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value; ApplyPlaceholder(); }
        }

        public override string Text
        {
            get { return inner != null ? inner.Text : base.Text; }
            set { if (inner != null) inner.Text = value; else base.Text = value; }
        }

        public static EdenField Wrap(Control inner, int height = 0)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            var field = new EdenField();
            var parent = inner.Parent;
            var index = parent == null ? -1 : parent.Controls.GetChildIndex(inner);
            field.Bounds = inner.Bounds;
            field.Dock = inner.Dock;
            field.Anchor = inner.Anchor;
            field.Margin = inner.Margin;
            field.TabIndex = inner.TabIndex;
            field.Visible = inner.Visible;
            if (height > 0) field.Height = height;
            else if (field.Height < 32) field.Height = Theme.Dim.Field;
            field.Attach(inner);
            if (parent != null)
            {
                parent.Controls.Add(field);
                parent.Controls.SetChildIndex(field, index);
            }
            return field;
        }

        private void Attach(Control control)
        {
            inner = control;
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = Padding.Empty;
            control.Visible = true;
            control.BackColor = Theme.Surface;
            if (control.Font == null || !control.Font.Name.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase)) control.Font = Theme.Body;
            var textBox = control as TextBox;
            if (textBox != null) textBox.BorderStyle = BorderStyle.None;
            var numeric = control as NumericUpDown;
            if (numeric != null) numeric.BorderStyle = BorderStyle.None;
            if (control is DateTimePicker) bordered = false;
            Controls.Add(control);
            control.Enter += (s, e) => SetFocused(true);
            control.Leave += (s, e) => SetFocused(false);
            if (textBox != null)
            {
                if (textBox.IsHandleCreated) ApplyPlaceholder();
                else textBox.HandleCreated += (s, e) => ApplyPlaceholder();
            }
            PositionInner();
        }

        private void ApplyPlaceholder()
        {
            var textBox = inner as TextBox;
            if (textBox == null || textBox.Multiline || !textBox.IsHandleCreated) return;
            var text = !string.IsNullOrEmpty(placeholder) ? placeholder : textBox.Tag as string;
            try { SendMessage(textBox.Handle, EM_SETCUEBANNER, (IntPtr)1, text ?? ""); } catch { }
        }

        private void SetFocused(bool value) { focused = value; Invalidate(); }

        private void PositionInner()
        {
            if (inner == null) return;
            const int padX = 10;
            var textBox = inner as TextBox;
            if (textBox != null && textBox.Multiline)
            {
                inner.SetBounds(padX, Theme.Space.Sm, Math.Max(10, Width - padX * 2), Math.Max(10, Height - Theme.Space.Sm * 2));
                return;
            }
            inner.Width = Math.Max(10, Width - padX * 2);
            inner.Left = padX;
            inner.Top = Math.Max(0, (Height - inner.Height) / 2);
        }

        protected override void OnLayout(LayoutEventArgs levent) { PositionInner(); base.OnLayout(levent); }
        protected override void OnSizeChanged(EventArgs e) { PositionInner(); base.OnSizeChanged(e); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (inner != null) inner.BackColor = Enabled ? Theme.Surface : Theme.SurfaceSunken;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            if (!bordered) return;
            using (var path = Gfx.Round(rect, CornerRadius))
            using (var brush = new SolidBrush(Enabled ? Theme.Surface : Theme.SurfaceSunken))
                g.FillPath(brush, path);
            if (focused)
            {
                using (var path = Gfx.Round(new Rectangle(1, 1, Width - 3, Height - 3), Math.Max(0, CornerRadius - 1)))
                using (var pen = new Pen(Theme.Primary, 2f)) g.DrawPath(pen, path);
            }
            else
            {
                using (var path = Gfx.Round(rect, CornerRadius))
                using (var pen = new Pen(Theme.BorderStrong)) g.DrawPath(pen, path);
            }
        }
    }

    // =====================================================================================================================
    // EdenComboBox: lista desplegable de solo seleccion (DropDownList) con dibujo propio: borde redondeado, chevron, foco verde,
    // elementos con seleccion verde suave (sin el azul ni el gris de Windows). Mismas propiedades que ComboBox.
    // =====================================================================================================================
    public class EdenComboBox : ComboBox
    {
        private bool hover;

        public EdenComboBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            Font = Theme.Body;
            BackColor = Theme.Surface;
            IntegralHeight = false;
            ItemHeight = 34;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Gfx.FillParentBack(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var path = Gfx.Round(rect, Theme.Radius.Control))
            using (var brush = new SolidBrush(Enabled ? Theme.Surface : Theme.SurfaceSunken))
                g.FillPath(brush, path);
            if (Focused || DroppedDown)
            {
                using (var path = Gfx.Round(new Rectangle(1, 1, Width - 3, Height - 3), Theme.Radius.Control - 1))
                using (var pen = new Pen(Theme.Primary, 2f)) g.DrawPath(pen, path);
            }
            else
            {
                using (var path = Gfx.Round(rect, Theme.Radius.Control))
                using (var pen = new Pen(hover ? Theme.Muted : Theme.BorderStrong)) g.DrawPath(pen, path);
            }

            var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : "";
            var textRect = new Rectangle(Theme.Space.Md, 0, Math.Max(0, Width - Theme.Space.Md - 34), Height);
            TextRenderer.DrawText(g, text, Font, textRect, Enabled ? Theme.Ink : Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            var cx = Width - 18; var cy = Height / 2;
            using (var pen = new Pen(Enabled ? Theme.Muted : Theme.TextDisabled, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var brush = new SolidBrush(selected ? Theme.GreenSoft : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            var bounds = new Rectangle(e.Bounds.X + Theme.Space.Md, e.Bounds.Y, Math.Max(0, e.Bounds.Width - Theme.Space.Md - 4), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, bounds, Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnDropDownClosed(EventArgs e) { Invalidate(); base.OnDropDownClosed(e); }
        protected override void OnDropDown(EventArgs e) { Invalidate(); base.OnDropDown(e); }
    }

    // =====================================================================================================================
    // Pill: insignia de estado (capsula con fondo suave y texto fuerte del tono). Tamaño automatico.
    // =====================================================================================================================
    public class Pill : Control
    {
        private Tone tone = Tone.Neutral;
        private bool showDot;

        public Pill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.FieldLabel;
            Margin = new Padding(0);
            Size = new Size(60, Theme.Dim.Pill);
        }

        public Tone Tone { get { return tone; } set { tone = value; Invalidate(); } }
        public bool ShowDot { get { return showDot; } set { showDot = value; FitSize(); Invalidate(); } }

        public override string Text { get { return base.Text; } set { base.Text = value; FitSize(); Invalidate(); } }

        private void FitSize() { Size = GetPreferredSize(Size.Empty); }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text ?? "", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Size(text.Width + Theme.Space.Md * 2 + (showDot ? 14 : 0), Theme.Dim.Pill);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var strong = Tones.Strong(tone);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Gfx.Round(rect, Height / 2))
            using (var brush = new SolidBrush(Tones.Soft(tone))) g.FillPath(brush, path);
            var left = Theme.Space.Md;
            if (showDot)
            {
                using (var brush = new SolidBrush(strong)) g.FillEllipse(brush, left - 2, Height / 2 - 3, 6, 6);
                left += 12;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left - 2 + (showDot ? 0 : 2), 0, Math.Max(0, Width - left - Theme.Space.Md + 2), Height), strong,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // =====================================================================================================================
    // Banner: mensaje en linea (estado, completado, advertencia, error). Fondo suave del tono, franja y circulo fuertes, texto fuerte.
    // La altura se ajusta al texto segun el ancho (si Dock es Top/Bottom o AutoHeight = true).
    // =====================================================================================================================
    public class Banner : Control
    {
        private Tone tone = Tone.Info;
        private bool adjusting;

        public Banner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            Height = 44;
            AutoHeight = true;
        }

        public Tone Tone { get { return tone; } set { tone = value; Invalidate(); } }
        public bool AutoHeight { get; set; }

        public override string Text { get { return base.Text; } set { base.Text = value; FitHeight(); Invalidate(); } }

        private const int LeftInset = 44;

        private int TextHeight(int width)
        {
            var size = TextRenderer.MeasureText(Text ?? "", Font, new Size(Math.Max(20, width - LeftInset - Theme.Space.Lg), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return size.Height;
        }

        private void FitHeight()
        {
            if (!AutoHeight || adjusting || Width <= 0) return;
            adjusting = true;
            try { var h = Math.Max(44, TextHeight(Width) + Theme.Space.Md * 2); if (Height != h) Height = h; }
            finally { adjusting = false; }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitHeight(); }
        protected override void OnPaintBackground(PaintEventArgs e) { Gfx.FillParentBack(this, e.Graphics); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var strong = Tones.Strong(tone);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Gfx.Round(rect, Theme.Radius.Control + 2))
            using (var brush = new SolidBrush(Tones.Soft(tone))) g.FillPath(brush, path);

            // Icono: circulo fuerte con simbolo blanco.
            var cx = 22; var cy = Math.Min(Height / 2, 22);
            using (var brush = new SolidBrush(strong)) g.FillEllipse(brush, cx - 9, cy - 9, 18, 18);
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (tone)
                {
                    case Tone.Success: g.DrawLines(pen, new[] { new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 4, cy - 3) }); break;
                    case Tone.Danger: g.DrawLine(pen, cx - 3, cy - 3, cx + 3, cy + 3); g.DrawLine(pen, cx + 3, cy - 3, cx - 3, cy + 3); break;
                    case Tone.Warning: g.DrawLine(pen, cx, cy - 4, cx, cy + 1); g.DrawLine(pen, cx, cy + 4, cx, cy + 4); break;
                    default: g.DrawLine(pen, cx, cy - 1, cx, cy + 4); g.DrawLine(pen, cx, cy - 4, cx, cy - 4); break;
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(LeftInset, Theme.Space.Md, Math.Max(0, Width - LeftInset - Theme.Space.Lg), Math.Max(0, Height - Theme.Space.Md * 2)), strong,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // =====================================================================================================================
    // EmptyState: estado vacio estandar (titulo + sugerencia + accion opcional), centrado. Dock = Fill por defecto.
    // Theme.EmptyState("Todavía no hay recibos", "Cuando cobre una venta, aparecerá aquí.", "Ir a Ventas") -> .Action es el boton.
    // =====================================================================================================================
    public class EmptyState : Control
    {
        private string title = "", hint = "", actionText;
        private EdenButton action;

        public EmptyState()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
        }

        public string Title { get { return title; } set { title = value ?? ""; Relayout(); } }
        public string Hint { get { return hint; } set { hint = value ?? ""; Relayout(); } }
        public EdenButton Action { get { return action; } }
        public string ActionText
        {
            get { return actionText; }
            set
            {
                actionText = value;
                if (string.IsNullOrEmpty(value)) { if (action != null) { Controls.Remove(action); action.Dispose(); action = null; } }
                else
                {
                    if (action == null) { action = Theme.Button(value, ButtonVariant.Secondary); action.Width = 200; Controls.Add(action); }
                    else action.Text = value;
                }
                Relayout();
            }
        }

        private const int MaxTextWidth = 420;

        private void Measure(out Size titleSize, out Size hintSize, out int total)
        {
            var width = Math.Min(MaxTextWidth, Math.Max(80, Width - Theme.Space.Xxl * 2));
            titleSize = TextRenderer.MeasureText(title, Theme.Subhead, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
            hintSize = hint.Length == 0 ? Size.Empty : TextRenderer.MeasureText(hint, Theme.Body, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
            total = titleSize.Height + (hint.Length == 0 ? 0 : Theme.Space.Sm + hintSize.Height) + (action == null ? 0 : Theme.Space.Lg + action.Height);
        }

        private void Relayout()
        {
            if (action != null)
            {
                Size t, h; int total; Measure(out t, out h, out total);
                var top = Math.Max(0, (Height - total) / 2);
                var y = top + t.Height + (hint.Length == 0 ? 0 : Theme.Space.Sm + h.Height) + Theme.Space.Lg;
                action.Location = new Point((Width - action.Width) / 2, y);
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Size t, h; int total;
            Measure(out t, out h, out total);
            var width = Math.Min(MaxTextWidth, Math.Max(80, Width - Theme.Space.Xxl * 2));
            var left = (Width - width) / 2;
            var top = Math.Max(0, (Height - total) / 2);
            TextRenderer.DrawText(e.Graphics, title, Theme.Subhead, new Rectangle(left, top, width, t.Height), Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            if (hint.Length > 0)
                TextRenderer.DrawText(e.Graphics, hint, Theme.Body, new Rectangle(left, top + t.Height + Theme.Space.Sm, width, h.Height), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    // Separador de 1 px (token Divider). Horizontal por defecto (alto 1); Vertical = ancho 1.
    public class Divider : Control
    {
        private bool vertical;

        public Divider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 1;
            Margin = new Padding(0);
            TabStop = false;
        }

        public bool Vertical
        {
            get { return vertical; }
            set { vertical = value; if (value) Width = 1; else Height = 1; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Theme.Divider)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    // =====================================================================================================================
    // ScreenHeader: cabecera estandar de pantalla sobre el lienzo (sin banda verde): titulo (Heading) + subtitulo de contexto
    // (Body secundario) a la izquierda, zona de acciones a la derecha y un filo inferior. Dock = Top.
    //   var header = new ScreenHeader { Title = "Recibos", Subtitle = "Ventas cobradas, por fecha" };
    //   header.AddAction(primario); header.AddAction(secundario);   // el PRIMER boton añadido queda a la derecha del todo
    // =====================================================================================================================
    public class ScreenHeader : Panel
    {
        private readonly Label titleLabel;
        private readonly Label subtitleLabel;
        private readonly FlowLayoutPanel actions;
        private readonly Panel textHost;

        public ScreenHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = 88;
            BackColor = Theme.Canvas;
            Padding = new Padding(Theme.Space.Xxl, Theme.Space.Lg, Theme.Space.Xxl, Theme.Space.Lg);

            textHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            subtitleLabel = new Label { Dock = DockStyle.Fill, Font = Theme.Body, ForeColor = Theme.Muted, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, BackColor = Color.Transparent, Visible = false };
            titleLabel = new Label { Dock = DockStyle.Fill, Font = Theme.Heading, ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Color.Transparent };
            textHost.Controls.Add(subtitleLabel);
            textHost.Controls.Add(titleLabel);
            actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            Controls.Add(textHost);
            Controls.Add(actions);
        }

        public string Title { get { return titleLabel.Text; } set { titleLabel.Text = value; } }

        public string Subtitle
        {
            get { return subtitleLabel.Text; }
            set
            {
                subtitleLabel.Text = value ?? "";
                var has = !string.IsNullOrEmpty(value);
                subtitleLabel.Visible = has;
                if (has)
                {
                    titleLabel.Dock = DockStyle.Top; titleLabel.Height = 34; titleLabel.TextAlign = ContentAlignment.BottomLeft;
                }
                else { titleLabel.Dock = DockStyle.Fill; titleLabel.TextAlign = ContentAlignment.MiddleLeft; }
            }
        }

        public void AddAction(Control control)
        {
            // Ancho minimo del boton segun su texto (los botones nuevos nacen con el ancho por defecto de Windows).
            if (control is EdenButton) control.Width = Math.Max(Math.Max(control.Width, 120), control.GetPreferredSize(Size.Empty).Width);
            control.Margin = new Padding(Theme.Space.Sm, 0, 0, 0);
            actions.Controls.Add(control);
            CenterActions();
        }

        private void CenterActions()
        {
            if (actions == null) return;
            var tallest = 0;
            foreach (Control c in actions.Controls) tallest = Math.Max(tallest, c.Height);
            var room = Height - Padding.Vertical;
            actions.Padding = new Padding(0, Math.Max(0, (room - tallest) / 2), 0, 0);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); CenterActions(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
