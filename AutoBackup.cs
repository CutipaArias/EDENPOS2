using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace EdenPOS
{
    // ===== Copia de seguridad AUTOMÁTICA a una carpeta elegida por la dueña (pensada para OneDrive / Google Drive) =====
    // La configuración es local a cada PC y vive en settings.json (claves AutoBackup*), junto a las demás claves locales.
    // Nunca bloquea ni muestra ventanas: el resultado se guarda y los errores van a errors.log.

    public sealed class AutoBackupConfig
    {
        public bool Enabled { get; set; }
        public string Folder { get; set; }
        public int Keep { get; set; }
        public DateTime? LastAt { get; set; }        // último intento (correcto o no)
        public bool LastOk { get; set; }
        public string LastMessage { get; set; }
        public DateTime? LastOkAt { get; set; }      // última copia correcta
    }

    public sealed class AutoBackupResult
    {
        public bool Ran { get; set; }      // false si no se intentó (desactivada, ocupada o sin carpeta)
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
    }

    public static partial class AppData
    {
        public const int AutoBackupDefaultKeep = 30;
        public const int AutoBackupMaxKeep = 365;
        public const string AutoBackupExtension = ".edenbackup";   // la misma de Exportar copia

        private const string AbEnabled = "AutoBackupEnabled";
        private const string AbFolder = "AutoBackupFolder";
        private const string AbKeep = "AutoBackupKeep";
        private const string AbLastAt = "AutoBackupLastAt";
        private const string AbLastOk = "AutoBackupLastOk";
        private const string AbLastMessage = "AutoBackupLastMessage";
        private const string AbLastOkAt = "AutoBackupLastOkAt";
        private const string AbDate = "yyyy-MM-ddTHH:mm:ss";

        private static int autoBackupRunning;      // 0 = libre, 1 = copia en curso (Interlocked)
        private static Timer autoBackupTimer;
        private static string autoBackupLastLoggedError;
        private static DateTime? autoBackupLastFailAt;   // último fallo de esta sesión (por si settings.json no se pudo escribir)
        private static readonly TimeSpan AutoBackupRetryAfterFailure = TimeSpan.FromHours(2);

        // ---- Configuración local ------------------------------------------------------------------------------------------
        private static Dictionary<string, object> ReadLocalDictionary()
        {
            Dictionary<string, object> data = null;
            try
            {
                if (File.Exists(PathFor("settings")))
                    data = Json.DeserializeObject(File.ReadAllText(PathFor("settings"), Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch { }
            return data ?? new Dictionary<string, object>();
        }

        // Cambia solo las claves indicadas y conserva el resto del archivo (mismo patrón que SaveLocalValues). Escritura atómica.
        private static void UpdateLocalValues(IDictionary<string, object> changes)
        {
            lock (Sync)
            {
                // Lectura estricta: si settings.json existe pero no se puede leer o parsear, lanza y NO se escribe nada
                // (antes se escribía un diccionario vacío y se perdía la cadena de conexión).
                var data = ReadLocalSettingsStrict();
                foreach (var pair in changes) data[pair.Key] = pair.Value;
                Save("settings", data);
            }
        }

        private static DateTime? ParseAbDate(object value)
        {
            DateTime parsed;
            var text = value as string;
            if (text != null && DateTime.TryParseExact(text, AbDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return parsed;
            return null;
        }

        public static AutoBackupConfig GetAutoBackupConfig()
        {
            Dictionary<string, object> data;
            lock (Sync) data = ReadLocalDictionary();
            object value;
            var config = new AutoBackupConfig { Keep = AutoBackupDefaultKeep, Folder = "", LastMessage = "" };
            if (data.TryGetValue(AbEnabled, out value) && value is bool) config.Enabled = (bool)value;
            if (data.TryGetValue(AbFolder, out value) && value is string) config.Folder = (string)value;
            if (data.TryGetValue(AbKeep, out value))
            {
                try { config.Keep = Math.Max(1, Math.Min(AutoBackupMaxKeep, Convert.ToInt32(value, CultureInfo.InvariantCulture))); }
                catch { }
            }
            if (data.TryGetValue(AbLastAt, out value)) config.LastAt = ParseAbDate(value);
            if (data.TryGetValue(AbLastOk, out value) && value is bool) config.LastOk = (bool)value;
            if (data.TryGetValue(AbLastMessage, out value) && value is string) config.LastMessage = (string)value;
            if (data.TryGetValue(AbLastOkAt, out value)) config.LastOkAt = ParseAbDate(value);
            return config;
        }

        // Devuelve null si la carpeta sirve, o el motivo (en español) si no.
        public static string ValidateAutoBackupFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Elija la carpeta donde se guardarán las copias.";
            try
            {
                var full = Path.GetFullPath(folder.Trim()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var dataRoot = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                    return "Elija una carpeta fuera de la carpeta de datos de Edén POS.";
                if (!Directory.Exists(full)) return "La carpeta no existe o no está disponible.";
            }
            catch (Exception)
            {
                return "La ruta de la carpeta no es válida.";
            }
            return null;
        }

        // Guarda los ajustes (sin tocar el resultado de la última copia). Lanza ArgumentException si se activa con carpeta inválida.
        public static void SaveAutoBackupConfig(bool enabled, string folder, int keep)
        {
            folder = (folder ?? "").Trim();
            if (enabled)
            {
                var problem = ValidateAutoBackupFolder(folder);
                if (problem != null) throw new ArgumentException(problem);
            }
            UpdateLocalValues(new Dictionary<string, object>
            {
                { AbEnabled, enabled },
                { AbFolder, folder },
                { AbKeep, Math.Max(1, Math.Min(AutoBackupMaxKeep, keep)) }
            });
        }

        private static void SaveAutoBackupResult(bool ok, string message)
        {
            var changes = new Dictionary<string, object>
            {
                { AbLastAt, DateTime.Now.ToString(AbDate, CultureInfo.InvariantCulture) },
                { AbLastOk, ok },
                { AbLastMessage, message ?? "" }
            };
            if (ok) changes[AbLastOkAt] = DateTime.Now.ToString(AbDate, CultureInfo.InvariantCulture);
            UpdateLocalValues(changes);
        }

        // ---- Nombres y retención ------------------------------------------------------------------------------------------
        private static string AutoBackupMachineName()
        {
            var raw = Environment.MachineName ?? "";
            var sb = new StringBuilder();
            foreach (var c in raw) sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' ? c : '_');
            var name = sb.ToString().Trim('_', '-');
            return name.Length == 0 ? "PC" : (name.Length > 40 ? name.Substring(0, 40) : name);
        }

        internal static string AutoBackupFileName(DateTime now)
        {
            return "EdenPOS-copia-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + AutoBackupMachineName() + AutoBackupExtension;
        }

        private static Regex AutoBackupOwnPattern()
        {
            return new Regex("^EdenPOS-copia-\\d{8}-\\d{6}-" + Regex.Escape(AutoBackupMachineName()) + Regex.Escape(AutoBackupExtension) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Borra solo los archivos de ESTE equipo con el nombre exacto de una copia automática, dejando los "keep" más recientes
        // (el orden sale del sello de fecha del nombre, no de la fecha del archivo: las nubes la cambian al sincronizar).
        // Los .part propios abandonados (más de una hora) también se limpian. Nunca toca otra cosa.
        // keepFileName: la copia recién creada; nunca se borra, aunque otro archivo "del futuro" ordene por encima de ella.
        internal static int ApplyAutoBackupRetention(string folder, int keep, string keepFileName = null)
        {
            var pattern = AutoBackupOwnPattern();
            var partPattern = new Regex(pattern.ToString().TrimEnd('$') + "\\.part$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var deleted = 0;
            var own = new DirectoryInfo(folder).GetFiles().Where(f => pattern.IsMatch(f.Name) && !string.Equals(f.Name, keepFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // La copia nueva cuenta como una de las "keep" que se conservan.
            var othersToKeep = keepFileName == null ? Math.Max(1, keep) : Math.Max(1, keep) - 1;
            foreach (var old in own.Skip(othersToKeep))
            {
                try { old.Delete(); deleted++; }
                catch (Exception ex) { LogError(ex, "Copia automática: borrar copia antigua"); }
            }
            foreach (var stale in new DirectoryInfo(folder).GetFiles().Where(f => partPattern.IsMatch(f.Name) && DateTime.Now - f.LastWriteTime > TimeSpan.FromHours(1)))
            {
                try { stale.Delete(); } catch { }
            }
            return deleted;
        }

        // ---- Ejecución ----------------------------------------------------------------------------------------------------
        // Síncrona; nunca lanza. manual = true ignora la casilla "activada" (botón "Hacer copia ahora"). Llamar fuera del hilo de la interfaz.
        public static AutoBackupResult RunAutoBackup(bool manual)
        {
            if (Interlocked.CompareExchange(ref autoBackupRunning, 1, 0) != 0)
                return new AutoBackupResult { Ran = false, Ok = false, Message = "Ya hay una copia en curso." };
            string temporary = null;
            try
            {
                var config = GetAutoBackupConfig();
                if (!manual && !config.Enabled) return new AutoBackupResult { Ran = false, Ok = true, Message = "La copia automática está desactivada." };
                var problem = ValidateAutoBackupFolder(config.Folder);
                if (problem != null) throw new InvalidOperationException(problem);

                var folder = Path.GetFullPath(config.Folder.Trim());
                // Antes de exportar (consulta pesada) se comprueba que la carpeta admite escritura: se crea y borra un archivo propio.
                var probe = Path.Combine(folder, ".edenpos-prueba-" + Guid.NewGuid().ToString("N") + ".tmp");
                try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
                catch (Exception probeEx)
                {
                    try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                    throw new InvalidOperationException("No se puede escribir en la carpeta de copias (" + DescribeAnyError(probeEx) + ").", probeEx);
                }
                var finalPath = Path.Combine(folder, AutoBackupFileName(DateTime.Now));
                var partPath = finalPath + ".part";
                // 1) Se exporta a una carpeta temporal local; ExportBackup ya escribe en .tmp y solo cierra el archivo al terminar.
                temporary = Path.Combine(Path.GetTempPath(), "EdenPOS-autocopia-" + Guid.NewGuid().ToString("N") + AutoBackupExtension);
                var rows = ExportBackup(temporary);
                // 2) Se copia a la carpeta con extensión .part y solo se renombra al nombre definitivo cuando está completa:
                //    si la app se cierra a mitad, nunca queda un archivo a medias con nombre de copia.
                File.Copy(temporary, partPath, true);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partPath, finalPath);
                string retentionNote = "";
                try { ApplyAutoBackupRetention(folder, config.Keep, Path.GetFileName(finalPath)); }
                catch (Exception ex) { LogError(ex, "Copia automática: retención"); retentionNote = " (no se pudieron borrar las copias antiguas)"; }
                var message = "Copia guardada: " + Path.GetFileName(finalPath) + " (" + rows + " registros)" + retentionNote;
                autoBackupLastLoggedError = null;
                autoBackupLastFailAt = null;
                try { SaveAutoBackupResult(true, message); } catch (Exception ex) { LogError(ex, "Copia automática: guardar resultado"); }
                return new AutoBackupResult { Ran = true, Ok = true, Message = message, FilePath = finalPath };
            }
            catch (Exception ex)
            {
                var detail = DescribeAnyError(ex);
                // Con la carpeta caída el temporizador espera 2 h entre intentos fallidos: se registra una sola vez el mismo error seguido.
                if (autoBackupLastLoggedError != detail) { LogError(ex, "Copia automática"); autoBackupLastLoggedError = detail; }
                autoBackupLastFailAt = DateTime.Now;
                try { SaveAutoBackupResult(false, detail); } catch (Exception saveEx) { LogError(saveEx, "Copia automática: guardar resultado del fallo"); }
                return new AutoBackupResult { Ran = true, Ok = false, Message = detail };
            }
            finally
            {
                try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { }
                Interlocked.Exchange(ref autoBackupRunning, 0);
            }
        }

        // En segundo plano. "done" (opcional) se llama en un hilo del pool: la pantalla debe pasar a su hilo (BeginInvoke).
        public static void RunAutoBackupAsync(bool manual, Action<AutoBackupResult> done)
        {
            Task.Run(() =>
            {
                AutoBackupResult result;
                try { result = RunAutoBackup(manual); }
                catch (Exception ex) { result = new AutoBackupResult { Ran = true, Ok = false, Message = ex.Message }; }
                if (done != null)
                {
                    try { done(result); } catch (Exception ex) { LogError(ex, "Copia automática: aviso de resultado"); }
                }
            });
        }

        // ---- Temporizador diario ------------------------------------------------------------------------------------------
        public static void StartAutoBackupScheduler()
        {
            lock (Sync)
            {
                if (autoBackupTimer != null) return;
                // Primera comprobación al minuto de arrancar (no compite con el inicio) y luego cada 30 minutos.
                autoBackupTimer = new Timer(AutoBackupTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
            }
        }

        public static void StopAutoBackupScheduler()
        {
            lock (Sync)
            {
                if (autoBackupTimer == null) return;
                autoBackupTimer.Dispose();
                autoBackupTimer = null;
            }
        }

        // Los callbacks de System.Threading.Timer corren en el pool, nunca en la interfaz.
        private static void AutoBackupTick(object state)
        {
            try
            {
                var config = GetAutoBackupConfig();
                if (!config.Enabled) return;
                if (config.LastOkAt.HasValue && DateTime.Now - config.LastOkAt.Value < TimeSpan.FromHours(24)) return;
                // Espera tras fallo: no repetir la exportación completa cada 30 min con el disco lleno o la carpeta caída.
                // (El botón manual y el cierre de turno no pasan por aquí y sí pueden intentar.)
                var lastFail = autoBackupLastFailAt;
                if (!config.LastOk && config.LastAt.HasValue && (!lastFail.HasValue || config.LastAt.Value > lastFail.Value)) lastFail = config.LastAt;
                if (lastFail.HasValue && DateTime.Now - lastFail.Value < AutoBackupRetryAfterFailure) return;
                RunAutoBackup(false);
            }
            catch (Exception ex) { LogError(ex, "Copia automática: temporizador"); }
        }
    }
}
