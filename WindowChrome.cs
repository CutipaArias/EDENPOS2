using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EdenPOS
{
    // Barra de título integrada: la ventana sigue siendo la de Windows (ajuste a la pantalla, minimizar, maximizar, cerrar,
    // Alt+Tab y el nombre en la barra de tareas siguen funcionando), pero la barra toma el color del lienzo de la app y no
    // dibuja título ni ícono, para que no se vea como una franja blanca ajena al diseño.
    //   · Título e ícono ocultos: SetWindowThemeAttribute (Windows 10 y 11). La barra de tareas los sigue mostrando.
    //   · Color de la barra y del borde: DwmSetWindowAttribute (Windows 11). En Windows 10 la barra queda del color del sistema.
    // Uso: protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); WindowChrome.Blend(this, Theme.Canvas); }
    internal static class WindowChrome
    {
        private const int WTA_NONCLIENT = 1;
        private const uint WTNCA_NODRAWCAPTION = 0x1, WTNCA_NODRAWICON = 0x2;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

        [StructLayout(LayoutKind.Sequential)]
        private struct WTA_OPTIONS { public uint dwFlags; public uint dwMask; }

        [DllImport("uxtheme.dll")]
        private static extern int SetWindowThemeAttribute(IntPtr hwnd, int eAttribute, ref WTA_OPTIONS pvAttribute, uint cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Marca del encabezado propio de un diálogo (DialogKit.Header): si el diálogo lo tiene, la barra no repite el título.
        public static readonly object HeaderTag = new object();

        // Para diálogos: al crearse la ventana, la barra toma el color de fondo del diálogo. El título de la barra se oculta
        // solo si el diálogo muestra su propio encabezado; si no, se deja (en texto Ink) para que se sepa qué ventana es.
        public static void Integrate(Form form)
        {
            if (form == null) return;
            EventHandler apply = (s, e) => Blend(form, form.BackColor, HasOwnHeader(form));
            form.HandleCreated += apply;
            if (form.IsHandleCreated) apply(form, EventArgs.Empty);
        }

        // Encabezado propio = DialogKit.Header, o cualquier etiqueta con la fuente de título (Theme.Title), como
        // "Nuevo usuario" o el nombre del artículo en los diálogos de Inventario.
        private static bool HasOwnHeader(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (ReferenceEquals(child.Tag, HeaderTag)) return true;
                if (child is Label && ReferenceEquals(child.Font, Theme.Title)) return true;
                if (HasOwnHeader(child)) return true;
            }
            return false;
        }

        public static void Blend(Form form, Color caption, bool hideTitle = true)
        {
            if (form == null || !form.IsHandleCreated) return;
            if (hideTitle)
            {
                try
                {
                    var options = new WTA_OPTIONS { dwFlags = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON, dwMask = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON };
                    SetWindowThemeAttribute(form.Handle, WTA_NONCLIENT, ref options, (uint)Marshal.SizeOf(typeof(WTA_OPTIONS)));
                }
                catch (Exception) { }   // sin temas visuales: queda la barra normal
            }
            else
            {
                var text = Theme.Luminance(caption) < 0.3 ? Theme.SidebarText : Theme.Ink;
                var textRef = text.R | (text.G << 8) | (text.B << 16);
                try { DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref textRef, sizeof(int)); }
                catch (Exception) { }
            }

            var colorRef = caption.R | (caption.G << 8) | (caption.B << 16);   // COLORREF = 0x00BBGGRR
            try
            {
                // Barra oscura: modo oscuro de la ventana para que minimizar/maximizar/cerrar se dibujen en blanco.
                var dark = Theme.Luminance(caption) < 0.3 ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
                DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            }
            catch (Exception) { }   // Windows 10 o anterior: el color no se aplica y no pasa nada
        }
    }
}
