using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MySqlConnector;

namespace EdenPOS
{
    public enum SupabaseReplicaState { Disabled, Waiting, Running, UpToDate, Error, Blocked }

    // Configuración local de esta PC (settings.json). Nunca incluye la contraseña.
    public sealed class SupabaseReplicaConfig
    {
        public bool Enabled { get; set; }
        public bool IsConfigured { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Database { get; set; }
        public string User { get; set; }
        public bool HasPassword { get; set; }
        public string CaPath { get; set; }          // ruta efectiva (la guardada o la predeterminada)
        public string DefaultCaPath { get; set; }
    }

    // El evento SupabaseReplicaStatusChanged se dispara desde un hilo de fondo: la interfaz debe usar Invoke/BeginInvoke.
    public sealed class SupabaseReplicaStatus
    {
        public SupabaseReplicaState State { get; set; }
        public string Message { get; set; }           // texto corto en español para mostrar
        public bool Enabled { get; set; }
        public bool Running { get; set; }
        public DateTime? LastSuccess { get; set; }    // hora local de la última pasada correcta
        public int LastPassRows { get; set; }         // filas subidas (nuevas, cambiadas y borradas) en la última pasada correcta
        public string LastError { get; set; }
        public DateTime? NextRun { get; set; }
        public string ThisPc { get; set; }
        public string RemoteLastPc { get; set; }      // _replica_info.ultima_pc (la última PC que subió)
        public string RemoteLastUpload { get; set; }  // _replica_info.ultima_subida
        public bool BlockedByMassDelete { get; set; } // la última pasada no se aplicó porque borraría demasiadas filas en Supabase
        public int PendingDeletes { get; set; }       // filas que esa pasada habría borrado (0 si no está bloqueada por borrado masivo)
        public bool OtherPcUploads { get { return !string.IsNullOrEmpty(RemoteLastPc) && !string.Equals(RemoteLastPc, ThisPc, StringComparison.OrdinalIgnoreCase); } }
    }

    public sealed class ReplicaResult
    {
        public bool Ok { get; set; }
        public bool Skipped { get; set; }             // true: no se ejecutó (ya había una pasada en curso o la réplica está desactivada)
        public bool Blocked { get; set; }             // Supabase contiene datos de otra base, o el cortacircuitos de borrado masivo se activó
        public bool BlockedByMassDelete { get; set; } // Blocked por borrado masivo (ver PendingDeletes)
        public int PendingDeletes { get; set; }       // filas que se habrían borrado en Supabase
        public int UploadedRows { get; set; }         // filas nuevas o cambiadas subidas
        public int DeletedRows { get; set; }          // filas borradas en Supabase
        public int TotalRows { get { return UploadedRows + DeletedRows; } }
        public string Message { get; set; }
        public TimeSpan Duration { get; set; }
    }

    // Vista previa de lo que hay en Supabase antes de restaurar (GetSupabaseRestorePreview).
    public sealed class SupabaseRestorePreview
    {
        public Dictionary<string, int> RowsPerTable { get; set; }
        public DateTime? LastUpload { get; set; }     // _replica_info.ultima_subida (hora local de la PC que subió)
        public string LastPc { get; set; }            // _replica_info.ultima_pc
        public bool CanRestore { get; set; }
        public string Problem { get; set; }           // motivo en español cuando CanRestore es false
    }

    internal class ReplicaBlockedException : Exception
    {
        public ReplicaBlockedException(string message) : base(message) { }
    }

    internal sealed class ReplicaMassDeleteException : ReplicaBlockedException
    {
        public int PendingDeletes { get; private set; }
        public ReplicaMassDeleteException(int pendingDeletes)
            : base("La base de esta PC tiene muchos menos datos que la réplica (se borrarían " + pendingDeletes.ToString(CultureInfo.InvariantCulture) +
                   " filas en Supabase). Si es correcto, pulse «Subir de todos modos»; si no, use «Restaurar desde Supabase».")
        { PendingDeletes = pendingDeletes; }
    }

    // Se escribe en errors.log con el texto ya enmascarado (sin cadena de conexión ni contraseña).
    internal sealed class ReplicaLoggedException : Exception
    {
        private readonly string text;
        public ReplicaLoggedException(string text) : base(text) { this.text = text; }
        public override string ToString() { return text; }
    }

    // Lo ya subido: por tabla, clave -> hash de la fila. Se guarda en <carpeta de datos>\supabase-replica.state.json.
    internal sealed class ReplicaStateFile
    {
        public int Version = 1;
        public string BaseId = "";
        public Dictionary<string, ReplicaTableStateFile> Tables = new Dictionary<string, ReplicaTableStateFile>();
    }

    internal sealed class ReplicaTableStateFile
    {
        public string Hash;
        public Dictionary<string, string> Rows = new Dictionary<string, string>();
    }

    internal sealed class ReplicaTableInfo
    {
        public ReplicaTable Table;
        public int[] KeyIndexes;
        public int[] KeyKinds;      // 0 = texto, 1 = int, 2 = long
        public string SelectSql;
    }

    internal sealed class ReplicaPlan
    {
        public ReplicaTableInfo Info;
        public bool Unchanged;
        public int LocalCount;       // filas locales leídas de la tabla
        public string NewHash;
        public List<object[]> Upserts = new List<object[]>();
        public List<string> UpsertKeys = new List<string>();
        public List<string> UpsertHashes = new List<string>();
        public List<object[]> Deletes = new List<object[]>();
        public List<string> DeleteKeys = new List<string>();
        public Dictionary<string, string> NewRows;   // solo cuando la tabla aún no tenía estado
    }

    public static partial class AppData
    {
        private const string ReplicaBlockedMessage = "Supabase contiene datos de otra base. Use Restaurar desde Supabase o vacíe la réplica.";
        private const string ReplicaStateFileName = "supabase-replica.state.json";
        private static readonly byte[] ReplicaEntropy = Encoding.UTF8.GetBytes("EdenPOS-SUPABASE-v1");
        private static readonly int[] ReplicaBackoffMinutes = { 1, 2, 5, 15, 30 };
        private static readonly TimeSpan ReplicaNormalInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ReplicaSoonDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ReplicaFirstDelay = TimeSpan.FromMinutes(1);

        public static event EventHandler SupabaseReplicaStatusChanged;

        // Para pruebas: sustituye al destino real (SupabaseTarget).
        internal static Func<IReplicaTarget> ReplicaTargetFactoryOverride = null;

        private static readonly object replicaLock = new object();
        private static int replicaBusy;
        private static bool replicaActive;
        private static Timer replicaTimer;
        private static DateTime replicaDue = DateTime.MaxValue;
        private static bool replicaPending;
        private static int replicaFailures;
        private static DateTime replicaLastInfoWrite = DateTime.MinValue;
        private static ReplicaStateFile replicaState;
        private static List<ReplicaTableInfo> replicaTables;

        private static SupabaseReplicaState replicaStatusState = SupabaseReplicaState.Disabled;
        private static string replicaStatusMessage = "Réplica desactivada.";
        private static bool replicaEnabledCache;
        private static DateTime? replicaLastSuccess;
        private static int replicaLastRows;
        private static string replicaLastError = "";
        private static DateTime? replicaNextRun;
        private static string replicaRemotePc = "";
        private static string replicaRemoteUpload = "";
        private static bool replicaBlockedMass;
        private static int replicaPendingDeletes;
        private static string[] replicaSecrets = new string[0];   // cadena de conexión y contraseña vistas, para enmascararlas

        // ===================== API pública =====================

        public static SupabaseReplicaConfig GetSupabaseReplicaConfig()
        {
            var data = ReadLocalSettingsStrict();
            var config = new SupabaseReplicaConfig
            {
                Enabled = ReplicaBool(data, "SupabaseReplicaEnabled"),
                DefaultCaPath = ReplicaDefaultCaPath(),
                Port = 5432,
                Database = "postgres",
                Host = "",
                User = ""
            };
            var custom = ReplicaString(data, "SupabaseCaPath");
            config.CaPath = string.IsNullOrWhiteSpace(custom) ? config.DefaultCaPath : custom;
            var text = ReplicaUnprotect(ReplicaString(data, "SupabaseConnectionProtected"));
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var builder = new Npgsql.NpgsqlConnectionStringBuilder(text);
                    config.IsConfigured = !string.IsNullOrWhiteSpace(builder.Host);
                    config.Host = builder.Host ?? "";
                    config.Port = builder.Port;
                    config.Database = builder.Database ?? "postgres";
                    config.User = builder.Username ?? "";
                    config.HasPassword = !string.IsNullOrEmpty(builder.Password);
                }
                catch (ArgumentException) { config.IsConfigured = false; }
            }
            return config;
        }

        public static void SaveSupabaseReplicaConfig(bool enabled, string host, int port, string database, string user, string passwordOrNullToKeep, string caPath)
        {
            host = (host ?? "").Trim();
            database = (database ?? "").Trim();
            user = (user ?? "").Trim();
            caPath = (caPath ?? "").Trim();
            string protectedConnection = null;   // null = no tocar la cadena guardada

            if (host.Length == 0)
            {
                if (enabled) throw new ArgumentException("Indique el servidor (host) de Supabase.");
            }
            else
            {
                protectedConnection = ReplicaProtect(ReplicaBuildConnectionString(host, port, database, user, passwordOrNullToKeep, enabled));
            }

            lock (Sync)
            {
                ReplicaUpdateSettings(data =>
                {
                    data["SupabaseReplicaEnabled"] = enabled;
                    if (protectedConnection != null) data["SupabaseConnectionProtected"] = protectedConnection;
                    data["SupabaseCaPath"] = caPath;
                });
            }
            replicaEnabledCache = enabled;
            ReplicaConfigChanged(enabled);
        }

        public static string TestSupabaseConnection()
        {
            bool enabled; string connection, ca;
            ReplicaReadConfig(out enabled, out connection, out ca);
            return ReplicaTest(connection, ca);
        }

        // Prueba valores aún sin guardar (passwordOrNullToKeep = null usa la contraseña ya guardada).
        public static string TestSupabaseConnection(string host, int port, string database, string user, string passwordOrNullToKeep, string caPath)
        {
            var connection = ReplicaBuildConnectionString((host ?? "").Trim(), port, (database ?? "").Trim(), (user ?? "").Trim(), passwordOrNullToKeep, true);
            caPath = (caPath ?? "").Trim();
            return ReplicaTest(connection, caPath.Length == 0 ? ReplicaDefaultCaPath() : caPath);
        }

        public static SupabaseReplicaStatus GetSupabaseReplicaStatus()
        {
            lock (replicaLock)
            {
                return new SupabaseReplicaStatus
                {
                    State = replicaStatusState,
                    Message = replicaStatusMessage,
                    Enabled = replicaEnabledCache,
                    Running = Volatile.Read(ref replicaBusy) != 0,
                    LastSuccess = replicaLastSuccess,
                    LastPassRows = replicaLastRows,
                    LastError = replicaLastError,
                    NextRun = replicaNextRun,
                    ThisPc = Environment.MachineName,
                    RemoteLastPc = replicaRemotePc,
                    RemoteLastUpload = replicaRemoteUpload,
                    BlockedByMassDelete = replicaBlockedMass,
                    PendingDeletes = replicaPendingDeletes
                };
            }
        }

        // Síncrono: pensado para Task.Run desde la interfaz. No lanza: devuelve el resultado (Ok=false con el motivo).
        public static ReplicaResult RunSupabaseReplicaNow()
        {
            return RunReplicaPass(false);
        }

        // allowMassDelete=true desactiva el cortacircuitos de borrado masivo solo para esta pasada («Subir de todos modos»).
        public static ReplicaResult RunSupabaseReplicaNow(bool allowMassDelete)
        {
            return RunReplicaPass(allowMassDelete);
        }

        // Lee Supabase (sin lock (Sync) ni replicaLock) y dice qué contiene y si es seguro restaurar desde ahí.
        // Lanza InvalidOperationException si no hay conexión configurada o no se puede leer Supabase.
        public static SupabaseRestorePreview GetSupabaseRestorePreview()
        {
            bool enabled; string connection, ca;
            ReplicaReadConfig(out enabled, out connection, out ca);
            if (ReplicaTargetFactoryOverride == null && string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Configure primero la conexión con Supabase.");
            try
            {
                var tables = ReplicaGetTables();
                var counts = new Dictionary<string, int>();
                string lastUpload, lastPc;
                using (var target = ReplicaCreateTarget(connection, ca))
                {
                    target.EnsureReady();
                    foreach (var info in tables) counts[info.Table.Name] = target.ReadAll(info.Table).Count();
                    lastUpload = (target.GetInfo("ultima_subida") ?? "").Trim();
                    lastPc = (target.GetInfo("ultima_pc") ?? "").Trim();
                }
                return ReplicaBuildPreview(counts, lastUpload, lastPc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("No se pudo leer Supabase: " + (ex is MySqlException ? DescribeDatabaseError((MySqlException)ex) : ReplicaFirstLine(ex.Message)));
            }
        }

        private static SupabaseRestorePreview ReplicaBuildPreview(Dictionary<string, int> counts, string lastUpload, string lastPc)
        {
            var preview = new SupabaseRestorePreview { RowsPerTable = counts, LastPc = lastPc ?? "", CanRestore = true, Problem = "" };
            DateTime parsed;
            if (!string.IsNullOrEmpty(lastUpload) &&
                DateTime.TryParseExact(lastUpload, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                preview.LastUpload = parsed;

            if (counts.Values.Sum() == 0)
            {
                preview.CanRestore = false;
                preview.Problem = "Supabase no contiene datos para restaurar.";
            }
            else if (string.IsNullOrEmpty(lastUpload))
            {
                preview.CanRestore = false;
                preview.Problem = "La réplica de Supabase está incompleta: ninguna subida llegó a terminar, así que no es seguro restaurar desde ella.";
            }
            else
            {
                var missing = new[] { "rol", "usuario", "configuracion" }.Where(t => !counts.ContainsKey(t) || counts[t] == 0).ToList();
                if (missing.Count > 0)
                {
                    preview.CanRestore = false;
                    preview.Problem = "La réplica de Supabase está incompleta: no tiene filas en " + string.Join(", ", missing) + ". No es seguro restaurar desde ella.";
                }
            }
            return preview;
        }

        // Llamar tras ventas, cierres de caja, etc. Barato y sin bloqueo: adelanta la próxima pasada a ~15 s (antirrebote: no la retrasa).
        public static void RequestReplicaSoon()
        {
            try
            {
                lock (replicaLock)
                {
                    if (!replicaActive || !replicaEnabledCache) return;
                    if (Volatile.Read(ref replicaBusy) != 0) { replicaPending = true; return; }
                    if (replicaFailures > 0) return; // en espera creciente por un fallo: no se adelanta
                    if (replicaDue > DateTime.UtcNow + ReplicaSoonDelay) ReplicaScheduleLocked(ReplicaSoonDelay);
                }
            }
            catch { }
        }

        public static void StartSupabaseReplica()
        {
            try
            {
                bool enabled; string connection, ca;
                ReplicaReadConfig(out enabled, out connection, out ca);
                // Tras ventas, cierres de caja, etc. adelanta la pasada. La réplica solo lee la base local y no dispara DataChanged: no hay bucle.
                DataChanged -= ReplicaOnDataChanged;
                DataChanged += ReplicaOnDataChanged;
                lock (replicaLock)
                {
                    replicaActive = true;
                    replicaEnabledCache = enabled;
                    if (replicaTimer == null) replicaTimer = new Timer(ReplicaTick, null, Timeout.Infinite, Timeout.Infinite);
                    if (enabled) ReplicaScheduleLocked(ReplicaFirstDelay);
                }
                if (!enabled) ReplicaSetStatus(SupabaseReplicaState.Disabled, "Réplica desactivada.");
                else ReplicaSetStatus(SupabaseReplicaState.Waiting, "Esperando la primera subida.");
            }
            catch (Exception ex) { ReplicaLog(ex, "Iniciar la réplica de Supabase"); }
        }

        private static void ReplicaOnDataChanged(object sender, EventArgs e) { RequestReplicaSoon(); }

        public static void StopSupabaseReplica()
        {
            DataChanged -= ReplicaOnDataChanged;
            lock (replicaLock)
            {
                replicaActive = false;
                replicaDue = DateTime.MaxValue;
                replicaNextRun = null;
                var timer = replicaTimer;
                replicaTimer = null;
                if (timer != null) timer.Dispose();
            }
        }

        // Reemplaza los datos de MariaDB por los de Supabase. Antes guarda una copia local automática
        // (carpeta "respaldos-previos" junto a la carpeta de datos). Devuelve las filas restauradas.
        public static int RestoreFromSupabase()
        {
            bool enabled; string connection, ca;
            ReplicaReadConfig(out enabled, out connection, out ca);
            if (ReplicaTargetFactoryOverride == null && string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Configure primero la conexión con Supabase.");

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (Interlocked.CompareExchange(ref replicaBusy, 1, 0) != 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException("Hay una subida a Supabase en curso. Inténtelo de nuevo en unos segundos.");
                Thread.Sleep(250);
            }
            ReplicaSetStatus(SupabaseReplicaState.Running, "Restaurando desde Supabase…");
            try
            {
                var tables = ReplicaGetTables();
                var remote = new Dictionary<string, List<object[]>>();
                string remoteBase;
                using (var target = ReplicaCreateTarget(connection, ca))
                {
                    target.EnsureReady();
                    remoteBase = (target.GetInfo("base_id") ?? "").Trim();
                    foreach (var info in tables)
                    {
                        var rows = target.ReadAll(info.Table).ToList();
                        foreach (var row in rows)
                            if (row == null || row.Length != info.Table.Columns.Length)
                                throw new InvalidDataException("Supabase devolvió una fila incompleta en la tabla " + info.Table.Name + ".");
                        remote[info.Table.Name] = rows;
                    }
                    // Misma verificación que la vista previa, sobre los datos que de verdad se van a restaurar.
                    var preview = ReplicaBuildPreview(remote.ToDictionary(p => p.Key, p => p.Value.Count),
                        (target.GetInfo("ultima_subida") ?? "").Trim(), (target.GetInfo("ultima_pc") ?? "").Trim());
                    if (!preview.CanRestore) throw new InvalidOperationException(preview.Problem);
                    if (remoteBase.Length == 0)
                    {
                        remoteBase = Guid.NewGuid().ToString("N");
                        target.SetInfo("base_id", remoteBase);
                    }
                }

                // Copia local previa: si no se puede crear, no se toca la base.
                string backupPath;
                try { backupPath = ReplicaMakePreRestoreBackup(); }
                catch (Exception ex)
                {
                    ReplicaLog(ex, "Copia previa a restaurar desde Supabase");
                    throw new InvalidOperationException("No se pudo crear la copia local previa (" + ex.Message + "). No se restauró nada.", ex);
                }

                var dictionaries = new Dictionary<string, List<Dictionary<string, object>>>();
                foreach (var info in tables)
                {
                    var list = new List<Dictionary<string, object>>();
                    foreach (var row in remote[info.Table.Name])
                    {
                        var item = new Dictionary<string, object>();
                        for (var i = 0; i < info.Table.Columns.Length; i++) item[info.Table.Columns[i]] = ReplicaRestoreValue(row[i]);
                        list.Add(item);
                    }
                    dictionaries[info.Table.Name] = list;
                }

                int totalRows;
                lock (Sync)
                {
                    var local = LoadLocalSettings();
                    var lookupEnabled = !string.IsNullOrWhiteSpace(UnprotectSecret(local.DniApiTokenProtected));
                    using (var db = SqlConnectionFactory.Open())
                    {
                        totalRows = RestoreTables(db, dictionaries, lookupEnabled);
                        // RestoreTables ya registró la auditoría de la restauración: se deja una sola, con el texto de Supabase.
                        try
                        {
                            using (var command = new MySqlCommand("UPDATE auditoria SET detalle = @detalle WHERE accion = @accion AND detalle LIKE 'Restauración de copia:%' ORDER BY id_auditoria DESC LIMIT 1", db))
                            {
                                command.Parameters.AddWithValue("@detalle", "Restaurado desde Supabase: " + totalRows + " filas. Copia previa: " + Path.GetFileName(backupPath));
                                command.Parameters.AddWithValue("@accion", AuditActions.CopiaRestaurada);
                                command.ExecuteNonQuery();
                            }
                        }
                        catch (Exception ex) { ReplicaLog(ex, "Texto de la auditoría de la restauración desde Supabase"); }
                    }
                }

                // Desde aquí los datos YA están restaurados.
                var notes = new List<string>();
                var warnings = new List<string>();
                lock (Sync)
                {
                    try { if (MigrateDefaults()) notes.Add(DefaultOwnerNote); }
                    catch (Exception ex) { ReplicaLog(ex, "Completar datos base tras restaurar desde Supabase"); warnings.Add("no se pudieron completar los datos base (" + DescribeAnyError(ex) + ")"); }
                    try { using (var db = SqlConnectionFactory.Open()) AdjustBackupAutoIncrement(db); }
                    catch (Exception ex) { ReplicaLog(ex, "Ajustar contadores tras restaurar desde Supabase"); warnings.Add("no se pudieron ajustar los contadores de identificadores (" + DescribeAnyError(ex) + ")"); }
                }
                // Estado vacío: la siguiente pasada reconcilia contra las claves remotas (sube lo local y borra solo lo que sobre en Supabase).
                try { ReplicaResetStateAfterRestore(remoteBase); }
                catch (Exception ex)
                {
                    ReplicaLog(ex, "Reiniciar el estado de la réplica tras restaurar");
                    lock (replicaLock) replicaState = new ReplicaStateFile { BaseId = remoteBase };
                    try { ReplicaUpdateSettings(d => d["SupabaseReplicaBaseId"] = remoteBase); } catch { }
                }
                lock (replicaLock)
                {
                    replicaPending = true;
                    replicaFailures = 0;
                    replicaLastError = "";
                    replicaBlockedMass = false;
                    replicaPendingDeletes = 0;
                }
                ReplicaSetStatus(SupabaseReplicaState.Waiting, "Datos restaurados desde Supabase.");
                return FinishRestore(totalRows, new List<KeyValuePair<string, byte[]>>(), notes, warnings);
            }
            catch (Exception ex) when (!(ex is BackupPostRestoreException))
            {
                ReplicaSetStatus(SupabaseReplicaState.Error, "No se pudo restaurar: " + ReplicaFirstLine(ex.Message));
                throw;
            }
            finally
            {
                Volatile.Write(ref replicaBusy, 0);
                ReplicaAfterPass(false);
            }
        }

        // ===================== Configuración local =====================

        private static string ReplicaDefaultCaPath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "supabase-ca.crt"); }

        private static string ReplicaString(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static bool ReplicaBool(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        // Misma protección que la cadena de MySQL (DPAPI, usuario actual) con su propia entropía.
        // No se usa ProtectSecret/UnprotectSecret: están ligadas al token de DNI (entropía propia y la variable EDENPOS_DNI_TOKEN).
        private static string ReplicaProtect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), ReplicaEntropy, DataProtectionScope.CurrentUser));
        }

        private static string ReplicaUnprotect(string protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), ReplicaEntropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        private static void ReplicaReadConfig(out bool enabled, out string connection, out string caPath)
        {
            var data = ReadLocalSettingsStrict();
            enabled = ReplicaBool(data, "SupabaseReplicaEnabled");
            connection = ReplicaUnprotect(ReplicaString(data, "SupabaseConnectionProtected"));
            ReplicaRememberSecrets(connection, null);
            caPath = ReplicaString(data, "SupabaseCaPath");
            if (string.IsNullOrWhiteSpace(caPath)) caPath = ReplicaDefaultCaPath();
        }

        // Lectura estricta + escritura atómica (tmp + File.Replace); conserva las demás claves de settings.json.
        private static void ReplicaUpdateSettings(Action<Dictionary<string, object>> change)
        {
            lock (Sync)
            {
                var data = ReadLocalSettingsStrict();
                change(data);
                Directory.CreateDirectory(DataDirectory);
                AtomicFile.WriteAllText(PathFor("settings"), PrettyJson(Json.Serialize(data)));
            }
        }

        private static string ReplicaBuildConnectionString(string host, int port, string database, string user, string passwordOrNullToKeep, bool requirePassword)
        {
            if (host.Length == 0) throw new ArgumentException("Indique el servidor (host) de Supabase.");
            if (port < 1 || port > 65535) throw new ArgumentException("El puerto debe estar entre 1 y 65535.");
            if (user.Length == 0) throw new ArgumentException("Indique el usuario de Supabase.");
            if (database.Length == 0) database = "postgres";
            var password = passwordOrNullToKeep;
            if (string.IsNullOrEmpty(password))
            {
                var data = ReadLocalSettingsStrict();
                var stored = ReplicaUnprotect(ReplicaString(data, "SupabaseConnectionProtected"));
                password = "";
                if (stored.Length > 0)
                {
                    try { password = new Npgsql.NpgsqlConnectionStringBuilder(stored).Password ?? ""; }
                    catch (ArgumentException) { password = ""; }
                }
            }
            if (requirePassword && string.IsNullOrEmpty(password)) throw new ArgumentException("Indique la contraseña de Supabase.");
            ReplicaRememberSecrets(null, password);
            try
            {
                var builder = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Port = port,
                    Database = database,
                    Username = user,
                    Password = password,
                    SslMode = Npgsql.SslMode.Require,
                    Pooling = false,
                    Timeout = 15,
                    CommandTimeout = 120
                };
                var built = builder.ConnectionString;
                ReplicaRememberSecrets(built, password);
                return built;
            }
            // Sin excepción interna ni su texto: el mensaje del generador de cadenas puede citar la cadena de conexión.
            catch (ArgumentException) { throw new ArgumentException("Los datos de conexión no son válidos. Revise servidor, puerto, base de datos, usuario y contraseña."); }
        }

        private static IReplicaTarget ReplicaCreateTarget(string connection, string caPath)
        {
            var factory = ReplicaTargetFactoryOverride;
            if (factory != null) return factory();
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Configure primero la conexión con Supabase.");
            if (string.IsNullOrWhiteSpace(caPath) || !File.Exists(caPath))
                throw new FileNotFoundException("No se encontró el certificado de Supabase (CA): " + caPath);
            return new SupabaseTarget(connection, caPath);
        }

        private static string ReplicaTest(string connection, string caPath)
        {
            try
            {
                using (var target = ReplicaCreateTarget(connection, caPath))
                {
                    target.EnsureReady();
                    var remoteBase = (target.GetInfo("base_id") ?? "").Trim();
                    var pc = (target.GetInfo("ultima_pc") ?? "").Trim();
                    var when = (target.GetInfo("ultima_subida") ?? "").Trim();
                    var text = new StringBuilder("Conexión correcta con Supabase. ");
                    if (remoteBase.Length == 0) text.Append("La réplica está vacía y lista para usarse.");
                    else
                    {
                        var local = ReplicaLocalBaseId();
                        text.Append(local.Length > 0 && local == remoteBase ? "Contiene la réplica de esta base de datos." : "Contiene datos de otra base de datos.");
                        if (pc.Length > 0) text.Append(" Última subida: ").Append(when.Length > 0 ? when : "(sin fecha)").Append(" desde ").Append(pc).Append('.');
                    }
                    return text.ToString();
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex)
            {
                var detail = ReplicaFirstLine(ex.Message);
                throw new InvalidOperationException(detail.StartsWith("No se pudo conectar con Supabase", StringComparison.OrdinalIgnoreCase)
                    ? detail : "No se pudo conectar con Supabase: " + detail);
            }
        }

        private static string ReplicaLocalBaseId()
        {
            try
            {
                var state = ReplicaEnsureState();
                if (!string.IsNullOrEmpty(state.BaseId)) return state.BaseId;
                return ReplicaString(ReadLocalSettingsStrict(), "SupabaseReplicaBaseId");
            }
            catch { return ""; }
        }

        private static void ReplicaRememberSecrets(string connection, string password)
        {
            var list = new List<string>(replicaSecrets);
            if (!string.IsNullOrWhiteSpace(connection))
            {
                list.Add(connection);
                try { var pw = new Npgsql.NpgsqlConnectionStringBuilder(connection).Password; if (!string.IsNullOrEmpty(pw)) list.Add(pw); } catch { }
            }
            if (!string.IsNullOrEmpty(password)) list.Add(password);
            replicaSecrets = list.Where(x => x.Length >= 4).Distinct().Take(12).ToArray();
        }

        // Quita de un texto la cadena de conexión y la contraseña (conocidas) y cualquier "Password=..." / "Pwd=...".
        private static string ReplicaMask(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var secret in replicaSecrets.OrderByDescending(x => x.Length))
                text = text.Replace(secret, "***");
            return System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(password|pwd)\s*=\s*[^;\r\n]*", "$1=***");
        }

        // errors.log nunca recibe la cadena de conexión ni la contraseña: se escribe una copia enmascarada de la excepción.
        private static void ReplicaLog(Exception exception, string context)
        {
            try { LogError(exception == null ? null : new ReplicaLoggedException(ReplicaMask(exception.ToString())), context); }
            catch { }
        }

        private static string ReplicaFirstLine(string text)
        {
            text = ReplicaMask(text);
            if (string.IsNullOrEmpty(text)) return "error desconocido";
            var index = text.IndexOfAny(new[] { '\r', '\n' });
            return index > 0 ? text.Substring(0, index) : text;
        }

        // ===================== Programación =====================

        private static void ReplicaConfigChanged(bool enabled)
        {
            lock (replicaLock)
            {
                if (!replicaActive) return;
                replicaFailures = 0;
                if (enabled) ReplicaScheduleLocked(ReplicaSoonDelay);
                else
                {
                    replicaDue = DateTime.MaxValue;
                    replicaNextRun = null;
                    if (replicaTimer != null) replicaTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            if (!enabled) ReplicaSetStatus(SupabaseReplicaState.Disabled, "Réplica desactivada.");
            else ReplicaSetStatus(SupabaseReplicaState.Waiting, "Esperando la próxima subida.");
        }

        private static void ReplicaScheduleLocked(TimeSpan delay)
        {
            if (!replicaActive || replicaTimer == null) return;
            replicaDue = DateTime.UtcNow + delay;
            replicaNextRun = DateTime.Now + delay;
            replicaTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private static void ReplicaTick(object unused)
        {
            try
            {
                if (!replicaActive) return;
                RunReplicaPass(false);   // el temporizador nunca pasa allowMassDelete=true
            }
            catch (Exception ex) { ReplicaLog(ex, "Pasada de la réplica de Supabase"); }
        }

        private static void ReplicaAfterPass(bool skipped)
        {
            try
            {
                lock (replicaLock)
                {
                    if (!replicaActive || skipped || !replicaEnabledCache) return;
                    TimeSpan delay;
                    if (replicaFailures > 0) delay = TimeSpan.FromMinutes(ReplicaBackoffMinutes[Math.Min(replicaFailures - 1, ReplicaBackoffMinutes.Length - 1)]);
                    else delay = replicaPending ? ReplicaSoonDelay : ReplicaNormalInterval;
                    replicaPending = false;
                    ReplicaScheduleLocked(delay);
                }
            }
            catch { }
        }

        private static void ReplicaSetStatus(SupabaseReplicaState state, string message)
        {
            lock (replicaLock)
            {
                replicaStatusState = state;
                replicaStatusMessage = message;
            }
            var handler = SupabaseReplicaStatusChanged;
            if (handler == null) return;
            try { handler(null, EventArgs.Empty); } catch (Exception ex) { ReplicaLog(ex, "Evento de estado de la réplica de Supabase"); }
        }

        // ===================== Una pasada =====================

        private static ReplicaResult RunReplicaPass(bool allowMassDelete)
        {
            if (Interlocked.CompareExchange(ref replicaBusy, 1, 0) != 0)
                return new ReplicaResult { Skipped = true, Message = "Ya hay una subida en curso." };
            var watch = Stopwatch.StartNew();
            ReplicaResult result;
            var skipped = false;
            try
            {
                bool enabled; string connection, ca;
                ReplicaReadConfig(out enabled, out connection, out ca);
                lock (replicaLock) replicaEnabledCache = enabled;
                if (!enabled)
                {
                    skipped = true;
                    lock (replicaLock) { replicaBlockedMass = false; replicaPendingDeletes = 0; }
                    ReplicaSetStatus(SupabaseReplicaState.Disabled, "Réplica desactivada.");
                    return new ReplicaResult { Skipped = true, Message = "La réplica con Supabase está desactivada." };
                }
                ReplicaSetStatus(SupabaseReplicaState.Running, "Subiendo cambios a Supabase…");
                int uploaded, deleted;
                ReplicaExecutePass(connection, ca, allowMassDelete, out uploaded, out deleted);
                watch.Stop();
                lock (replicaLock)
                {
                    replicaBlockedMass = false;
                    replicaPendingDeletes = 0;
                    replicaFailures = 0;
                    replicaLastSuccess = DateTime.Now;
                    replicaLastRows = uploaded + deleted;
                    replicaLastError = "";
                }
                result = new ReplicaResult
                {
                    Ok = true,
                    UploadedRows = uploaded,
                    DeletedRows = deleted,
                    Duration = watch.Elapsed,
                    Message = uploaded + deleted == 0 ? "Sin cambios: Supabase ya está al día." : "Se subieron " + uploaded + " filas y se borraron " + deleted + "."
                };
                ReplicaSetStatus(SupabaseReplicaState.UpToDate, uploaded + deleted == 0 ? "Réplica al día." : "Réplica al día (" + (uploaded + deleted) + " filas en la última subida).");
            }
            catch (ReplicaBlockedException ex)
            {
                watch.Stop();
                ReplicaRegisterFailure(ex, true);
                var mass = ex as ReplicaMassDeleteException;
                lock (replicaLock)
                {
                    replicaBlockedMass = mass != null;
                    replicaPendingDeletes = mass != null ? mass.PendingDeletes : 0;
                }
                result = new ReplicaResult
                {
                    Blocked = true, Message = ex.Message, Duration = watch.Elapsed,
                    BlockedByMassDelete = mass != null, PendingDeletes = mass != null ? mass.PendingDeletes : 0
                };
                ReplicaSetStatus(SupabaseReplicaState.Blocked, ex.Message);
            }
            catch (Exception ex)
            {
                watch.Stop();
                ReplicaRegisterFailure(ex, false);
                var text = ex is MySqlException ? DescribeDatabaseError((MySqlException)ex) : ReplicaFirstLine(ex.Message);
                result = new ReplicaResult { Message = "No se pudo subir a Supabase: " + text, Duration = watch.Elapsed };
                lock (replicaLock) { replicaLastError = text; replicaBlockedMass = false; replicaPendingDeletes = 0; }
                ReplicaSetStatus(SupabaseReplicaState.Error, result.Message);
            }
            finally
            {
                Volatile.Write(ref replicaBusy, 0);
            }
            ReplicaAfterPass(skipped);
            return result;
        }

        // Devuelve true si es el primer error de la racha (solo entonces se escribe en errors.log).
        private static bool ReplicaRegisterFailure(Exception ex, bool blocked)
        {
            bool first;
            lock (replicaLock)
            {
                replicaFailures++;
                first = replicaFailures == 1;
                replicaLastError = blocked ? ex.Message : ReplicaFirstLine(ex.Message);
            }
            if (first) ReplicaLog(ex, "Réplica de Supabase");
            return first;
        }

        private static void ReplicaExecutePass(string connection, string ca, bool allowMassDelete, out int uploaded, out int deleted)
        {
            uploaded = 0;
            deleted = 0;
            var tables = ReplicaGetTables();
            var state = ReplicaEnsureState();

            using (var target = ReplicaCreateTarget(connection, ca))
            {
                target.EnsureReady();
                var remoteBase = (target.GetInfo("base_id") ?? "").Trim();
                var pc = (target.GetInfo("ultima_pc") ?? "").Trim();
                var when = (target.GetInfo("ultima_subida") ?? "").Trim();
                lock (replicaLock) { replicaRemotePc = pc; replicaRemoteUpload = when; }

                var localBase = state.BaseId ?? "";
                if (localBase.Length == 0) localBase = ReplicaString(ReadLocalSettingsStrict(), "SupabaseReplicaBaseId");

                var fresh = false;
                if (remoteBase.Length == 0)
                {
                    // Supabase sin identificar (nunca usada o vaciada): solo se usa si de verdad está vacía.
                    foreach (var info in tables)
                        if (target.ReadAll(info.Table).Any()) throw new ReplicaBlockedException(ReplicaBlockedMessage);
                    if (localBase.Length == 0) localBase = Guid.NewGuid().ToString("N");
                    var newBase = localBase;
                    ReplicaUpdateSettings(d => d["SupabaseReplicaBaseId"] = newBase);
                    state.BaseId = localBase;
                    state.Tables.Clear();
                    ReplicaSaveState(state);
                    target.SetInfo("base_id", localBase);
                    fresh = true;
                }
                else if (localBase.Length == 0 || localBase != remoteBase)
                {
                    throw new ReplicaBlockedException(ReplicaBlockedMessage);
                }
                else if (state.BaseId != localBase)
                {
                    state.BaseId = localBase;
                    ReplicaSaveState(state);
                }

                // Tablas sin estado local (estado perdido o primera vez): se leen las claves remotas para poder calcular los borrados.
                var remoteKeys = new Dictionary<string, Dictionary<string, object[]>>();
                if (!fresh)
                    foreach (var info in tables)
                        if (!state.Tables.ContainsKey(info.Table.Name)) remoteKeys[info.Table.Name] = ReplicaReadRemoteKeys(target, info);

                var plans = ReplicaReadLocal(tables, state, remoteKeys);
                var noChanges = new List<object[]>();

                // Cortacircuitos: si la base local quedó vacía o es otra (XAMPP reinstalado, esquema recreado, copia pequeña), no se aplica
                // nada (ni altas ni borrados) salvo que el usuario lo confirme con «Subir de todos modos».
                if (!allowMassDelete)
                {
                    long pending = 0, known = 0, local = 0;
                    foreach (var plan in plans)
                    {
                        local += plan.LocalCount;
                        ReplicaTableStateFile known0;
                        Dictionary<string, object[]> keys0;
                        if (state.Tables.TryGetValue(plan.Info.Table.Name, out known0)) known += known0.Rows.Count;
                        else if (remoteKeys.TryGetValue(plan.Info.Table.Name, out keys0)) known += keys0.Count;
                        if (!plan.Unchanged) pending += plan.Deletes.Count;
                    }
                    if (pending > 0 && (pending > Math.Max(20, known * 0.3) || local < known * 0.5))
                        throw new ReplicaMassDeleteException((int)pending);
                }

                // Fase 1: altas y cambios, padres primero.
                foreach (var plan in plans)
                {
                    if (plan.Upserts.Count == 0) continue;
                    target.Apply(plan.Info.Table, plan.Upserts, noChanges);
                    uploaded += plan.Upserts.Count;
                    ReplicaTableStateFile st;
                    if (!state.Tables.TryGetValue(plan.Info.Table.Name, out st))
                    {
                        if (!fresh) continue; // sin estado y con claves remotas por revisar: el estado se crea tras la fase de borrados
                        st = new ReplicaTableStateFile();
                        state.Tables[plan.Info.Table.Name] = st;
                    }
                    for (var i = 0; i < plan.UpsertKeys.Count; i++) st.Rows[plan.UpsertKeys[i]] = plan.UpsertHashes[i];
                    ReplicaSaveState(state);
                }

                // Fase 2: borrados, hijas primero.
                for (var index = plans.Count - 1; index >= 0; index--)
                {
                    var plan = plans[index];
                    if (plan.Unchanged) continue;
                    var name = plan.Info.Table.Name;
                    if (plan.Deletes.Count > 0)
                    {
                        target.Apply(plan.Info.Table, noChanges, plan.Deletes);
                        deleted += plan.Deletes.Count;
                    }
                    ReplicaTableStateFile st;
                    if (!state.Tables.TryGetValue(name, out st))
                    {
                        st = new ReplicaTableStateFile { Rows = plan.NewRows ?? new Dictionary<string, string>() };
                        state.Tables[name] = st;
                    }
                    else foreach (var key in plan.DeleteKeys) st.Rows.Remove(key);
                    st.Hash = plan.NewHash;
                    ReplicaSaveState(state);
                }

                var now = DateTime.UtcNow;
                if (uploaded + deleted > 0 || now - replicaLastInfoWrite > TimeSpan.FromMinutes(30))
                {
                    target.SetInfo("ultima_pc", Environment.MachineName);
                    target.SetInfo("ultima_subida", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    replicaLastInfoWrite = now;
                    lock (replicaLock) { replicaRemotePc = Environment.MachineName; replicaRemoteUpload = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
                }
            }
        }

        private static Dictionary<string, object[]> ReplicaReadRemoteKeys(IReplicaTarget target, ReplicaTableInfo info)
        {
            var keys = new Dictionary<string, object[]>(StringComparer.Ordinal);
            foreach (var row in target.ReadAll(info.Table))
            {
                var key = new object[info.KeyIndexes.Length];
                for (var i = 0; i < key.Length; i++) key[i] = row[info.KeyIndexes[i]];
                keys[ReplicaKeyString(key)] = key;
            }
            return keys;
        }

        // Una sola lectura consistente de MariaDB (conexión propia, sin lock (Sync)): las ventas siguen funcionando.
        private static List<ReplicaPlan> ReplicaReadLocal(List<ReplicaTableInfo> tables, ReplicaStateFile state, Dictionary<string, Dictionary<string, object[]>> remoteKeys)
        {
            var plans = new List<ReplicaPlan>();
            using (var db = SqlConnectionFactory.Open())
            {
                RunSql(db, "START TRANSACTION WITH CONSISTENT SNAPSHOT");
                try
                {
                    using (var sha = SHA256.Create())
                        foreach (var info in tables)
                        {
                            ReplicaTableStateFile st;
                            state.Tables.TryGetValue(info.Table.Name, out st);
                            Dictionary<string, object[]> remote;
                            remoteKeys.TryGetValue(info.Table.Name, out remote);
                            plans.Add(ReplicaReadTable(db, sha, info, st, remote));
                        }
                }
                finally
                {
                    try { RunSql(db, "ROLLBACK"); } catch { }
                }
            }
            return plans;
        }

        private static ReplicaPlan ReplicaReadTable(MySqlConnection db, SHA256 sha, ReplicaTableInfo info, ReplicaTableStateFile st, Dictionary<string, object[]> remote)
        {
            var rows = new List<object[]>();
            var keys = new List<string>();
            var hashes = new List<string>();
            var text = new StringBuilder();
            var columns = info.Table.Columns.Length;
            sha.Initialize();
            using (var command = new MySqlCommand(info.SelectSql, db))
            {
                command.CommandTimeout = 300;
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var values = new object[columns];
                        for (var i = 0; i < columns; i++)
                        {
                            var value = reader.GetValue(i);
                            values[i] = value is DBNull ? null : value;
                        }
                        var key = ReplicaKeyString(info.KeyIndexes.Select(i => values[i]).ToArray());
                        text.Clear();
                        for (var i = 0; i < columns; i++) ReplicaAppendValue(text, values[i]);
                        var hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
                        rows.Add(values);
                        keys.Add(key);
                        hashes.Add(hash);
                    }
            }

            var tableText = new StringBuilder(keys.Count * 80);
            for (var i = 0; i < keys.Count; i++) tableText.Append(keys[i]).Append('\n').Append(hashes[i]).Append('\n');
            var plan = new ReplicaPlan { Info = info, LocalCount = keys.Count, NewHash =Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(tableText.ToString()))) };

            if (st != null && st.Hash != null && st.Hash == plan.NewHash)
            {
                plan.Unchanged = true; // la tabla no cambió desde la última pasada: no se compara fila a fila
                return plan;
            }

            var seen = new HashSet<string>(keys, StringComparer.Ordinal);
            if (st == null)
            {
                plan.NewRows = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);
                for (var i = 0; i < keys.Count; i++)
                {
                    plan.Upserts.Add(rows[i]);
                    plan.UpsertKeys.Add(keys[i]);
                    plan.UpsertHashes.Add(hashes[i]);
                    plan.NewRows[keys[i]] = hashes[i];
                }
                if (remote != null)
                    foreach (var pair in remote)
                        if (!seen.Contains(pair.Key)) { plan.Deletes.Add(pair.Value); plan.DeleteKeys.Add(pair.Key); }
            }
            else
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    string old;
                    if (st.Rows.TryGetValue(keys[i], out old) && old == hashes[i]) continue;
                    plan.Upserts.Add(rows[i]);
                    plan.UpsertKeys.Add(keys[i]);
                    plan.UpsertHashes.Add(hashes[i]);
                }
                foreach (var key in st.Rows.Keys)
                    if (!seen.Contains(key)) { plan.Deletes.Add(ReplicaParseKey(info, key)); plan.DeleteKeys.Add(key); }
            }
            return plan;
        }

        // ===================== Normalización y claves =====================

        private static string ReplicaKeyPart(object value)
        {
            if (value == null || value is DBNull) return "";
            var text = value as string;
            if (text != null) return text;
            if (value is bool) return (bool)value ? "1" : "0";
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (value is ulong) return ((ulong)value).ToString(CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture);
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string ReplicaKeyString(object[] key)
        {
            if (key.Length == 1) return ReplicaKeyPart(key[0]);
            return string.Join("\u001f", key.Select(ReplicaKeyPart));
        }

        private static object[] ReplicaParseKey(ReplicaTableInfo info, string key)
        {
            var parts = info.KeyIndexes.Length == 1 ? new[] { key } : key.Split('\u001f');
            if (parts.Length != info.KeyIndexes.Length) throw new InvalidDataException("Clave de réplica inválida en la tabla " + info.Table.Name + ".");
            var result = new object[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (info.KeyKinds[i] == 1) result[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
                else if (info.KeyKinds[i] == 2) result[i] = long.Parse(parts[i], CultureInfo.InvariantCulture);
                else result[i] = parts[i];
            }
            return result;
        }

        // Texto estable por valor (decimales invariantes sin ceros finales, fechas ISO, null) para el hash SHA-256 de la fila.
        private static void ReplicaAppendValue(StringBuilder sb, object value)
        {
            if (value == null || value is DBNull) { sb.Append("n;"); return; }
            var text = value as string;
            if (text != null) { sb.Append('s').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text); return; }
            if (value is bool) { sb.Append((bool)value ? "b1;" : "b0;"); return; }
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long)
            { sb.Append('i').Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is ulong) { sb.Append('i').Append(((ulong)value).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is decimal) { sb.Append('m').Append(((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is DateTime) { sb.Append('t').Append(((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is double) { sb.Append('f').Append(((double)value).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (value is float) { sb.Append('f').Append(((float)value).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            var bytes = value as byte[];
            if (bytes != null) { sb.Append('x').Append(Convert.ToBase64String(bytes)).Append(';'); return; }
            sb.Append('o').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
        }

        // ===================== Metadatos de MariaDB =====================

        // Las 16 tablas (orden de BackupTables: padres antes que hijas), con columnas y clave primaria leídas de information_schema.
        // Se calcula SIN replicaLock (abrir MariaDB puede tomar lock (Sync) y RequestReplicaSoon se llama dentro de Sync): el candado solo asigna.
        private static List<ReplicaTableInfo> ReplicaGetTables()
        {
            lock (replicaLock) { if (replicaTables != null) return replicaTables; }
            var built = ReplicaBuildTables();
            lock (replicaLock)
            {
                if (replicaTables == null) replicaTables = built;
                return replicaTables;
            }
        }

        private static List<ReplicaTableInfo> ReplicaBuildTables()
        {
            {
                var columns = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
                var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                using (var db = SqlConnectionFactory.Open())
                {
                    using (var command = new MySqlCommand(
                        "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION", db))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var table = ReplicaText(reader.GetValue(0));
                            List<KeyValuePair<string, string>> list;
                            if (!columns.TryGetValue(table, out list)) columns[table] = list = new List<KeyValuePair<string, string>>();
                            list.Add(new KeyValuePair<string, string>(ReplicaText(reader.GetValue(1)), ReplicaText(reader.GetValue(2)).ToLowerInvariant()));
                        }
                    using (var command = new MySqlCommand(
                        "SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'PRIMARY' ORDER BY TABLE_NAME, ORDINAL_POSITION", db))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var table = ReplicaText(reader.GetValue(0));
                            List<string> list;
                            if (!keys.TryGetValue(table, out list)) keys[table] = list = new List<string>();
                            list.Add(ReplicaText(reader.GetValue(1)));
                        }
                }

                var result = new List<ReplicaTableInfo>();
                foreach (var name in BackupTables)
                {
                    List<KeyValuePair<string, string>> cols;
                    List<string> pk;
                    if (!columns.TryGetValue(name, out cols)) throw new InvalidOperationException("Falta la tabla " + name + " en la base de datos.");
                    if (!keys.TryGetValue(name, out pk) || pk.Count == 0) throw new InvalidOperationException("La tabla " + name + " no tiene clave primaria.");
                    // El token de DNI (cifrado para esta PC) no se replica, igual que las copias de seguridad.
                    // El estado de bloqueo de las cuentas (RF-01) es local de cada instalación y tampoco se replica: así la réplica funciona igual aunque
                    // el esquema de Supabase aún no tenga esas columnas.
                    var kept = cols.Where(c => !(name == "configuracion" && c.Key == "dni_api_token_protected") &&
                                               !(name == "usuario" && (c.Key == "intentos_fallidos" || c.Key == "bloqueado_en")) &&
                                               // RN-09: por ahora la observación de cierre se queda solo en la base local (Supabase aún puede no tener la columna).
                                               !(name == "sesion_caja" && c.Key == "observacion_cierre") &&
                                               // RF-04 / RF-05: medio de los movimientos y Yape esperado/confirmado del cierre, por ahora solo en la base local
                                               // (Supabase aún puede no tener las columnas; devdb/supabase/01_esquema_replica.sql ya las agrega).
                                               !(name == "sesion_caja" && (c.Key == "yape_esperado" || c.Key == "yape_contado")) &&
                                               !(name == "movimiento_caja" && c.Key == "medio") &&
                                               // RF-06 / RNF-09: por ahora el teléfono y el consentimiento del cliente se quedan solo en la base local (Supabase aún puede no tener las columnas).
                                               !(name == "cliente" && (c.Key == "telefono" || c.Key == "consentimiento_datos" || c.Key == "consentimiento_fecha")) &&
                                               !(name == "configuracion" && c.Key == "simular_rechazo_comprobantes") &&
                                               // RF-19 / RN-06 / RN-07: cargos por pérdida y llaves fuera de uso, por ahora solo en la base local
                                               // (Supabase aún puede no tener las columnas; devdb/supabase/01_esquema_replica.sql ya las agrega).
                                               !(name == "configuracion" && (c.Key == "cargo_perdida_llave" || c.Key == "llaves_fuera_uso")) &&
                                               !(name == "producto" && c.Key == "cargo_perdida")).ToList();
                    var names = kept.Select(c => c.Key).ToArray();
                    var info = new ReplicaTableInfo
                    {
                        Table = new ReplicaTable { Name = name, Columns = names, KeyColumns = pk.ToArray() },
                        KeyIndexes = pk.Select(k => Array.FindIndex(names, n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase))).ToArray(),
                        KeyKinds = pk.Select(k =>
                        {
                            var type = cols.First(c => string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase)).Value;
                            return type == "bigint" ? 2 : (type == "int" || type == "smallint" || type == "mediumint" || type == "tinyint") ? 1 : 0;
                        }).ToArray()
                    };
                    if (info.KeyIndexes.Any(i => i < 0)) throw new InvalidOperationException("La clave de " + name + " no está entre sus columnas.");
                    info.SelectSql = "SELECT " + string.Join(", ", names.Select(n => "`" + n + "`")) + " FROM `" + name + "` ORDER BY " +
                                     string.Join(", ", pk.Select(k => "`" + k + "`"));
                    result.Add(info);
                }
                return result;
            }
        }

        private static string ReplicaText(object raw)
        {
            return raw is byte[] ? Encoding.UTF8.GetString((byte[])raw) : Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        // ===================== Estado local =====================

        // Estado en memoria; si no está, se lee del disco FUERA de replicaLock (E/S) y el candado solo asigna.
        private static ReplicaStateFile ReplicaEnsureState()
        {
            lock (replicaLock) { if (replicaState != null) return replicaState; }
            var loaded = ReplicaLoadState();
            lock (replicaLock)
            {
                if (replicaState == null) replicaState = loaded;
                return replicaState;
            }
        }

        private static string ReplicaStatePath() { return Path.Combine(DataDirectory, ReplicaStateFileName); }

        private static ReplicaStateFile ReplicaLoadState()
        {
            try
            {
                var path = ReplicaStatePath();
                if (File.Exists(path))
                {
                    var state = Json.Deserialize<ReplicaStateFile>(File.ReadAllText(path, Encoding.UTF8));
                    if (state != null)
                    {
                        if (state.Tables == null) state.Tables = new Dictionary<string, ReplicaTableStateFile>();
                        if (state.BaseId == null) state.BaseId = "";
                        foreach (var table in state.Tables.Values) if (table.Rows == null) table.Rows = new Dictionary<string, string>();
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                // Estado ilegible: se trata como perdido (la siguiente pasada reconcilia con Supabase sin perder nada).
                ReplicaLog(ex, "Leer el estado de la réplica de Supabase");
            }
            return new ReplicaStateFile();
        }

        private static void ReplicaSaveState(ReplicaStateFile state)
        {
            Directory.CreateDirectory(DataDirectory);
            AtomicFile.WriteAllText(ReplicaStatePath(), Json.Serialize(state));
        }

        // ===================== Restauración =====================

        private static object ReplicaRestoreValue(object value)
        {
            if (value == null || value is DBNull) return null;
            if (value is DateTimeOffset) value = ((DateTimeOffset)value).LocalDateTime;
            return BackupValue(value);
        }

        private static string ReplicaMakePreRestoreBackup()
        {
            var data = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(data);
            var folder = parent != null
                ? Path.Combine(parent, "respaldos-previos")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdenPOS", "respaldos-previos");
            // ExportBackup rechaza destinos dentro de la carpeta de datos.
            if ((Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdenPOS", "respaldos-previos");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "antes-de-restaurar-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");
            ExportBackup(path);
            try
            {
                foreach (var old in new DirectoryInfo(folder).GetFiles("antes-de-restaurar-*.zip").OrderByDescending(f => f.Name).Skip(10))
                    old.Delete();
            }
            catch { }
            return path;
        }

        // Tras restaurar NO se asume remoto == local (la restauración añade auditoría y filas base): el estado queda vacío y la
        // siguiente pasada lee las claves remotas, sube lo local y borra solo lo que sobre en Supabase.
        private static void ReplicaResetStateAfterRestore(string remoteBase)
        {
            var state = new ReplicaStateFile { BaseId = remoteBase };
            ReplicaUpdateSettings(d => d["SupabaseReplicaBaseId"] = remoteBase);
            ReplicaSaveState(state);
            lock (replicaLock) replicaState = state;
        }
    }
}
