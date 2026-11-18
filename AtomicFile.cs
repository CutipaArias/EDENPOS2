using System;
using System.IO;
using System.Text;

namespace EdenPOS
{
    internal static class AtomicFile
    {
        // El temporal se crea junto al destino para que el reemplazo ocurra en el mismo volumen.
        internal static void WriteAllText(string path, string text)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                // Un fallo no debe dejar temporales con tokens o cadenas de conexión.
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
