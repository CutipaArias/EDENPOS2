using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MySqlConnector;

namespace EdenPOS
{
    // RF-12 / RNF-07: impresión del recibo en la impresora térmica del local (papel de 80 mm).
    // El armado del contenido (BuildLines) es una función pura y testeable; el dibujo (Render) solo recorre esas líneas con Graphics.
    // Todas las medidas de dibujo están en centésimas de pulgada (la unidad de la página de impresión de Windows).

    public enum TicketLineStyle { Normal, Small, Bold, Title, Total, Warning }

    // Una línea del recibo impreso: texto a la izquierda (se parte en varias líneas si no cabe) y, opcionalmente, un importe a la derecha.
    public sealed class TicketLine
    {
        public TicketLineStyle Style { get; set; }
        public string Left { get; set; }
        public string Right { get; set; }
        public bool Center { get; set; }
        public bool IsSeparator { get; set; }

        public override string ToString()
        {
            if (IsSeparator) return new string('-', 32);
            return string.IsNullOrEmpty(Right) ? (Left ?? "") : (Left ?? "") + " | " + Right;
        }
    }

    // Configuración de impresión de ESTA PC (tabla impresora_pc, una fila por Environment.MachineName).
    public sealed class PrinterConfig
    {
        public string Equipo { get; set; }
        public string NombreImpresora { get; set; }
        public bool ImprimirAlCobrar { get; set; }
        public bool HasPrinter { get { return !string.IsNullOrWhiteSpace(NombreImpresora); } }
    }

    public static class ReceiptPrinting
    {
        // Papel de 80 mm = 3,15 pulgadas; área útil ~72 mm (2,83 pulgadas) con 4 mm de margen a cada lado.
        public const int PaperWidth = 315;
        public const float PrintableWidth = 283f;
        public const float SideMargin = (PaperWidth - PrintableWidth) / 2f;
        public const float TopMargin = 12f;
        public const float BottomMargin = 40f;     // avance para el corte del papel
        public const string AvisoNoValida = "Comprobante no válido como boleta electrónica: envío pendiente";
        public const string AvisoSimulada = "Facturación electrónica SIMULADA: no se envió nada a SUNAT ni a un OSE.";
        private const string FontName = "Arial";

        // ---- Contenido (sin Graphics ni base de datos) --------------------------------------------------------------------
        public static List<TicketLine> BuildLines(Receipt receipt, string storeName, string currencySymbol)
        {
            if (receipt == null) throw new ArgumentNullException("receipt");
            Func<decimal, string> money = value => (currencySymbol ?? "") + value.ToString("N2", CultureInfo.GetCultureInfo("es-PE"));
            var lines = new List<TicketLine>();
            Action<string, TicketLineStyle, bool> text = (value, style, center) => lines.Add(new TicketLine { Left = value, Style = style, Center = center });
            Action<string, string, TicketLineStyle> pair = (left, right, style) => lines.Add(new TicketLine { Left = left, Right = right, Style = style });
            Action separator = () => lines.Add(new TicketLine { IsSeparator = true });

            text(string.IsNullOrWhiteSpace(storeName) ? "Edén POS" : storeName.Trim(), TicketLineStyle.Title, true);
            text("Recibo N.º 2-" + receipt.Number.ToString(CultureInfo.InvariantCulture), TicketLineStyle.Bold, true);
            separator();
            text("Fecha: " + receipt.CreatedAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + "   Hora: " + receipt.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture), TicketLineStyle.Normal, false);
            text("Cajero: " + (string.IsNullOrWhiteSpace(receipt.Employee) ? "-" : receipt.Employee), TicketLineStyle.Normal, false);
            text("Cliente: " + (string.IsNullOrWhiteSpace(receipt.CustomerName) ? "Público general" : receipt.CustomerName), TicketLineStyle.Normal, false);
            if (!string.IsNullOrWhiteSpace(receipt.CustomerDni)) text("DNI: " + receipt.CustomerDni, TicketLineStyle.Normal, false);
            separator();

            pair("Cant.  Descripción", "Importe", TicketLineStyle.Small);
            foreach (var line in receipt.Lines ?? new List<ReceiptLine>())
            {
                pair(line.Quantity.ToString(CultureInfo.InvariantCulture) + " x " + line.Name, money(line.LineTotal), TicketLineStyle.Normal);
                if (line.Quantity > 1) text("      " + money(line.UnitPrice) + " c/u", TicketLineStyle.Small, false);
                if (!string.IsNullOrWhiteSpace(line.Comment)) text("      " + line.Comment.Trim(), TicketLineStyle.Small, false);
            }
            separator();

            if (receipt.Discount > 0m)
            {
                pair("Subtotal", money(receipt.Subtotal), TicketLineStyle.Normal);
                pair("Promoción: " + SaleCompleteDialog.PromotionName(receipt.PromotionId), "-" + money(receipt.Discount), TicketLineStyle.Normal);
            }
            pair("TOTAL", money(receipt.Total), TicketLineStyle.Total);

            var method = receipt.PaymentMethod ?? "";
            text("Medio de pago: " + (method.Length == 0 ? "-" : method), TicketLineStyle.Normal, false);
            if (method == "Efectivo")
            {
                pair("Recibido", money(receipt.AmountReceived), TicketLineStyle.Normal);
                pair("Vuelto", money(Math.Max(0m, receipt.AmountReceived - receipt.Total)), TicketLineStyle.Normal);
            }
            else if (method == "Mixto")
            {
                pair("Efectivo", money(receipt.CashPaid), TicketLineStyle.Normal);
                pair("Yape", money(receipt.YapePaid), TicketLineStyle.Normal);
            }
            else if (method == "Yape") pair("Yape", money(receipt.Total), TicketLineStyle.Normal);

            if (receipt.Status != null && receipt.Status != "Pagado")
            {
                separator();
                text("RECIBO " + receipt.Status.ToUpperInvariant() +
                     (receipt.RefundedAt.HasValue ? " EL " + receipt.RefundedAt.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : ""), TicketLineStyle.Warning, true);
            }

            // Boleta electrónica (simulada). RN-10: si no está Aceptada, el papel no vale como boleta.
            separator();
            var comp = receipt.Comprobante;
            if (comp != null)
            {
                text("Boleta electrónica " + comp.Documento, TicketLineStyle.Bold, false);
                text("Estado del envío: " + (comp.Estado ?? ComprobanteEstados.Pendiente) +
                     (comp.Estado == ComprobanteEstados.Rechazado && !string.IsNullOrWhiteSpace(comp.CodigoRespuesta) ? " (" + comp.CodigoRespuesta + ")" : ""), TicketLineStyle.Normal, false);
            }
            else text("Sin boleta electrónica registrada.", TicketLineStyle.Normal, false);
            if (comp == null || comp.Estado != ComprobanteEstados.Aceptado)
                text(AvisoNoValida, TicketLineStyle.Warning, true);
            text(AvisoSimulada, TicketLineStyle.Small, true);
            separator();
            text("¡Gracias por su visita!", TicketLineStyle.Bold, true);
            return lines;
        }

        public static string ToPlainText(IEnumerable<TicketLine> lines)
        {
            return string.Join(Environment.NewLine, System.Linq.Enumerable.Select(lines, x => x.ToString()));
        }

        // ---- Dibujo ---------------------------------------------------------------------------------------------------------
        // Fuentes en unidades "World" (= centésimas de pulgada en la página de impresión), para medir igual en impresora y en Bitmap.
        private sealed class TicketFonts : IDisposable
        {
            public readonly Font Normal, Small, Bold, Title, Total;

            public TicketFonts()
            {
                Normal = Make(8.5f, FontStyle.Regular);
                Small = Make(7f, FontStyle.Regular);
                Bold = Make(8.5f, FontStyle.Bold);
                Title = Make(11.5f, FontStyle.Bold);
                Total = Make(11f, FontStyle.Bold);
            }

            private static Font Make(float points, FontStyle style)
            {
                return new Font(FontName, points * 100f / 72f, style, GraphicsUnit.World);
            }

            public Font For(TicketLineStyle style)
            {
                switch (style)
                {
                    case TicketLineStyle.Small: return Small;
                    case TicketLineStyle.Bold: case TicketLineStyle.Warning: return Bold;
                    case TicketLineStyle.Title: return Title;
                    case TicketLineStyle.Total: return Total;
                    default: return Normal;
                }
            }

            public void Dispose() { Normal.Dispose(); Small.Dispose(); Bold.Dispose(); Title.Dispose(); Total.Dispose(); }
        }

        // Alto de cada línea (centésimas de pulgada) para el ancho útil dado.
        private static float MeasureLine(Graphics g, TicketFonts fonts, TicketLine line, float width)
        {
            if (line.IsSeparator) return 10f;
            var font = fonts.For(line.Style);
            using (var format = new StringFormat(StringFormatFlags.LineLimit))
            {
                var leftWidth = width;
                if (!string.IsNullOrEmpty(line.Right))
                    leftWidth = Math.Max(40f, width - g.MeasureString(line.Right, font, PointF.Empty, format).Width - 6f);
                var size = g.MeasureString(line.Left ?? "", font, new SizeF(leftWidth, 10000f), format);
                var height = Math.Max(size.Height, font.Size * 1.15f);
                if (line.Style == TicketLineStyle.Warning) height += 8f;   // recuadro
                return height + 1.5f;
            }
        }

        // Alto total del contenido (sin márgenes) para el papel de 80 mm.
        public static float MeasureHeight(Graphics g, IList<TicketLine> lines)
        {
            using (var fonts = new TicketFonts())
            {
                var total = 0f;
                foreach (var line in lines) total += MeasureLine(g, fonts, line, PrintableWidth);
                return total;
            }
        }

        // Dibuja desde la línea 'start' a partir de (x, y) hasta 'maxBottom'. Devuelve el índice de la siguiente línea por dibujar.
        public static int Render(Graphics g, IList<TicketLine> lines, int start, float x, float y, float width, float maxBottom)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var fonts = new TicketFonts())
            using (var pen = new Pen(Color.Black, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            using (var box = new Pen(Color.Black, 1.5f))
            using (var left = new StringFormat(StringFormatFlags.LineLimit))
            using (var center = new StringFormat(StringFormatFlags.LineLimit) { Alignment = StringAlignment.Center })
            using (var right = new StringFormat(StringFormatFlags.LineLimit | StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far })
            {
                var index = start;
                while (index < lines.Count)
                {
                    var line = lines[index];
                    var height = MeasureLine(g, fonts, line, width);
                    // Siempre cabe al menos una línea por página (evita un bucle sin fin con páginas diminutas).
                    if (index > start && y + height > maxBottom) break;
                    if (line.IsSeparator)
                        g.DrawLine(pen, x, y + height / 2f, x + width, y + height / 2f);
                    else
                    {
                        var font = fonts.For(line.Style);
                        var top = y + (line.Style == TicketLineStyle.Warning ? 4f : 0f);
                        var leftWidth = width;
                        if (!string.IsNullOrEmpty(line.Right))
                        {
                            var rightWidth = g.MeasureString(line.Right, font, PointF.Empty, right).Width;
                            leftWidth = Math.Max(40f, width - rightWidth - 6f);
                            g.DrawString(line.Right, font, Brushes.Black, new RectangleF(x + width - rightWidth - 2f, top, rightWidth + 2f, height), right);
                        }
                        g.DrawString(line.Left ?? "", font, Brushes.Black, new RectangleF(x, top, leftWidth, height), line.Center ? center : left);
                        if (line.Style == TicketLineStyle.Warning)
                            g.DrawRectangle(box, x + 1f, y + 1f, width - 2f, height - 3.5f);
                    }
                    y += height;
                    index++;
                }
                return index;
            }
        }

        // Página completa en un Bitmap (sin impresora): vista de muestra y pruebas. dpi = resolución del PNG.
        public static Bitmap RenderToBitmap(IList<TicketLine> lines, int dpi = 203)
        {
            float height;
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                PrepareBitmapGraphics(g, probe, dpi);
                height = MeasureHeight(g, lines);
            }
            var pageHeight = TopMargin + height + BottomMargin;
            var bitmap = new Bitmap((int)Math.Ceiling(PaperWidth * dpi / 100f), (int)Math.Ceiling(pageHeight * dpi / 100f));
            bitmap.SetResolution(dpi, dpi);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                PrepareBitmapGraphics(g, bitmap, dpi);
                Render(g, lines, 0, SideMargin, TopMargin, PrintableWidth, float.MaxValue);
            }
            return bitmap;
        }

        private static void PrepareBitmapGraphics(Graphics g, Bitmap bitmap, int dpi)
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.ScaleTransform(dpi / 100f, dpi / 100f);   // 1 unidad = 1/100 de pulgada, como en la impresora
        }

        // ---- Documento de impresión -------------------------------------------------------------------------------------------
        public static PrintDocument CreateDocument(Receipt receipt, string printerName)
        {
            var settings = AppData.Settings;
            var lines = BuildLines(receipt, settings.StoreName, settings.CurrencySymbol);
            float contentHeight;
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                PrepareBitmapGraphics(g, probe, 100);
                contentHeight = MeasureHeight(g, lines);
            }

            var document = new PrintDocument { DocumentName = "Recibo 2-" + receipt.Number.ToString(CultureInfo.InvariantCulture) };
            if (!string.IsNullOrWhiteSpace(printerName)) document.PrinterSettings.PrinterName = printerName;
            document.OriginAtMargins = false;
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.DefaultPageSettings.PaperSize = new PaperSize("Recibo 80 mm", PaperWidth, (int)Math.Ceiling(TopMargin + contentHeight + BottomMargin));
            var next = 0;
            document.BeginPrint += (s, e) => next = 0;
            document.PrintPage += (s, e) =>
            {
                // Con OriginAtMargins = false, el origen es la esquina del área imprimible: se descuenta el margen físico del controlador.
                var hardX = e.PageSettings.HardMarginX;
                var hardY = e.PageSettings.HardMarginY;
                var x = Math.Max(0f, SideMargin - hardX);
                var width = Math.Min(PrintableWidth, e.PageBounds.Width - 2f * Math.Max(hardX, SideMargin));
                if (width < 150f) width = PrintableWidth;
                var top = Math.Max(0f, (next == 0 ? TopMargin : 4f) - hardY);
                var bottom = e.PageBounds.Height - hardY - 4f;
                next = Render(e.Graphics, lines, next, x, top, width, bottom);
                e.HasMorePages = next < lines.Count;
            };
            return document;
        }

        // ---- Acciones de la interfaz ------------------------------------------------------------------------------------------
        // Imprime (o muestra la vista previa) con la impresora configurada en esta PC. Nunca lanza: los errores se avisan y registran.
        public static bool PrintReceipt(IWin32Window owner, Receipt receipt, bool preview)
        {
            if (receipt == null) return false;
            PrinterConfig config;
            try { config = AppData.GetPrinterConfig(); }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Leer la impresora de esta PC");
                Theme.Message(owner, Tone.Warning, ex is MySqlException ? AppData.DescribeDatabaseError((MySqlException)ex) : "No se pudo leer la configuración de la impresora. El detalle quedó en errors.log.", "Imprimir recibo");
                return false;
            }
            if (!preview && !config.HasPrinter)
            {
                Theme.Message(owner, Tone.Warning,
                    "No hay una impresora configurada en esta PC. Elíjala en Configuración → Impresora. El cobro quedó registrado; puede imprimir el recibo más tarde desde Recibos.",
                    "Imprimir recibo");
                return false;
            }
            if (config.HasPrinter && !IsInstalled(config.NombreImpresora))
            {
                Theme.Message(owner, Tone.Warning,
                    "La impresora \"" + config.NombreImpresora + "\" no está instalada o no está disponible en esta PC. Revise la conexión o elija otra en Configuración → Impresora. El cobro quedó registrado.",
                    "Imprimir recibo");
                if (!preview) return false;
            }

            // Estado actual de la boleta (el envío simulado corre en segundo plano y puede haber cambiado).
            try { receipt.Comprobante = AppData.GetComprobante(receipt.Id) ?? receipt.Comprobante; }
            catch (Exception ex) { AppData.LogError(ex, "Leer el estado de la boleta antes de imprimir"); }

            try
            {
                using (var document = CreateDocument(receipt, config.HasPrinter && IsInstalled(config.NombreImpresora) ? config.NombreImpresora : null))
                {
                    if (preview)
                    {
                        using (var dialog = new PrintPreviewDialog { Document = document, Width = 520, Height = 760, Text = "Vista previa del recibo", UseAntiAlias = true })
                            dialog.ShowDialog(owner);
                    }
                    else
                    {
                        document.PrintController = new StandardPrintController();
                        document.Print();
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidPrinterException || ex is ExternalException || ex is InvalidOperationException || ex is MySqlException || ex is ArgumentException)
            {
                AppData.LogError(ex, "Imprimir recibo 2-" + receipt.Number);
                Theme.Message(owner, Tone.Warning,
                    "No se pudo imprimir el recibo #2-" + receipt.Number + ". Revise que la impresora esté encendida, con papel y conectada. El cobro quedó registrado; puede reimprimirlo desde Recibos.",
                    "Imprimir recibo");
                return false;
            }
        }

        // Tras cobrar: imprime solo si "Imprimir recibo al cobrar" está activo en esta PC. No bloquea ni deshace el cobro.
        public static void PrintAfterSale(IWin32Window owner, Receipt receipt)
        {
            try
            {
                PrinterConfig config;
                try { config = AppData.GetPrinterConfig(); }
                catch (Exception ex) { AppData.LogError(ex, "Leer la impresora de esta PC tras cobrar"); return; }
                if (!config.ImprimirAlCobrar) return;
                PrintReceipt(owner, receipt, false);
            }
            catch (Exception ex)
            {
                AppData.LogError(ex, "Impresión automática tras cobrar");
            }
        }

        public static List<string> InstalledPrinters()
        {
            var result = new List<string>();
            try { foreach (string name in PrinterSettings.InstalledPrinters) result.Add(name); }
            catch (Exception ex) { AppData.LogError(ex, "Listar impresoras instaladas"); }
            return result;
        }

        private static bool IsInstalled(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            try { return new PrinterSettings { PrinterName = printerName }.IsValid; }
            catch { return false; }
        }
    }

    public static partial class AppData
    {
        public const int EquipoMaxLength = 64;
        public const int NombreImpresoraMaxLength = 200;

        public static string EquipoActual
        {
            get { return Clip(Environment.MachineName, EquipoMaxLength); }
        }

        // RF-12 / RNF-07: tabla de impresora por PC (devdb/migraciones/006_rf12_impresora.sql). Se crea al iniciar si falta.
        // No entra en las copias .edenbackup ni en la réplica de Supabase: el nombre de la impresora es propio de cada equipo.
        private static void EnsurePrinterTable()
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                try
                {
                    RunSql(connection,
                        "CREATE TABLE IF NOT EXISTS impresora_pc (" +
                        "equipo VARCHAR(64) PRIMARY KEY, " +
                        "nombre_impresora VARCHAR(200) NULL, " +
                        "imprimir_al_cobrar TINYINT(1) NOT NULL DEFAULT 0)");
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.TableAccessDenied || ex.ErrorCode == MySqlErrorCode.ColumnAccessDenied ||
                                                ex.ErrorCode == MySqlErrorCode.DatabaseAccessDenied || (int)ex.ErrorCode == 1227)   // 1142, 1143, 1044, 1227
                {
                    LogError(ex, "Migración: crear la tabla impresora_pc");
                    throw new InvalidOperationException(
                        "Falta actualizar la base de datos: ejecute los scripts de devdb\\migraciones (001, 002...) con un usuario que tenga permiso CREATE y ALTER.", ex);
                }
            }
        }

        public static PrinterConfig GetPrinterConfig()
        {
            var config = new PrinterConfig { Equipo = EquipoActual };
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand("SELECT nombre_impresora, imprimir_al_cobrar FROM impresora_pc WHERE equipo = @equipo", connection))
            {
                command.Parameters.AddWithValue("@equipo", config.Equipo);
                using (var reader = command.ExecuteReader())
                    if (reader.Read())
                    {
                        config.NombreImpresora = reader.IsDBNull(0) ? null : reader.GetString(0);
                        config.ImprimirAlCobrar = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) != 0;
                    }
            }
            return config;
        }

        public static void SavePrinterConfig(string printerName, bool printOnCharge)
        {
            var name = string.IsNullOrWhiteSpace(printerName) ? null : printerName.Trim();
            if (name != null && name.Length > NombreImpresoraMaxLength)
                throw new ArgumentException("El nombre de la impresora no puede superar " + NombreImpresoraMaxLength + " caracteres.");
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(
                "INSERT INTO impresora_pc (equipo, nombre_impresora, imprimir_al_cobrar) VALUES (@equipo, @nombre, @auto) " +
                "ON DUPLICATE KEY UPDATE nombre_impresora = VALUES(nombre_impresora), imprimir_al_cobrar = VALUES(imprimir_al_cobrar)", connection))
            {
                command.Parameters.AddWithValue("@equipo", EquipoActual);
                command.Parameters.AddWithValue("@nombre", (object)name ?? DBNull.Value);
                command.Parameters.AddWithValue("@auto", printOnCharge ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }
    }
}
