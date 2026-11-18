using System.IO;
using System.Text;

namespace EdenPOS
{
    internal static class ErrorLog
    {
        private static readonly object Sync = new object();
        private const long RotationSize = 2 * 1024 * 1024;

        internal static void Append(string path, string text)
        {
            // Los temporizadores, la réplica y la interfaz pueden registrar errores al mismo tiempo.
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length >= RotationSize)
                {
                    var previous = path + ".1";
                    if (File.Exists(previous)) File.Replace(path, previous, null);
                    else File.Move(path, previous);
                }
                File.AppendAllText(path, text, Encoding.UTF8);
            }
        }
    }
}
