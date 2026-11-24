using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace EdenPOS
{
    internal sealed class ReplicaTable
    {
        public string Name;              // nombre de tabla
        public string[] Columns;         // todas las columnas, en orden
        public string[] KeyColumns;      // clave primaria
    }

    internal interface IReplicaTarget : IDisposable
    {
        void EnsureReady();
        void Apply(ReplicaTable table, IList<object[]> upserts, IList<object[]> deleteKeys);
        IEnumerable<object[]> ReadAll(ReplicaTable table);
        void SetInfo(string key, string value);
        string GetInfo(string key);
        // Columnas que la tabla tiene en el destino (leídas en EnsureReady), o null si la tabla no existe allí.
        ICollection<string> GetColumnNames(string table);
    }

    // Destino de la réplica: PostgreSQL de Supabase (esquema "edenpos", ver devdb/supabase/01_esquema_replica.sql).
    internal sealed class SupabaseTarget : IReplicaTarget
    {
        internal const string SchemaName = "edenpos";
        internal const int BatchSize = 500;
        private const string MissingScriptMessage = "Falta ejecutar el script de Supabase (devdb\\supabase\\01_esquema_replica.sql en el SQL Editor de su proyecto).";

        // Tablas que debe tener el esquema de la réplica.
        internal static readonly string[] RequiredTables =
        {
            "rol", "usuario", "cliente", "producto", "producto_variante", "ticket_guardado", "casillero_llave",
            "ticket_linea", "sesion_caja", "movimiento_caja", "recibo", "recibo_linea", "comprobante",
            "movimiento_inventario", "auditoria", "configuracion", "_replica_info"
        };

        private static readonly Regex IdentifierPattern = new Regex("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

        private readonly NpgsqlDataSource dataSource;
        private readonly string secret;
        private readonly string hostLabel;
        private readonly object cacheLock = new object();
        // tabla -> columna -> tipo de PostgreSQL (data_type de information_schema)
        private Dictionary<string, Dictionary<string, string>> columnTypes;
        private bool disposed;
        // Último rechazo del certificado TLS (para explicar el motivo en el mensaje de error).
        private SslPolicyErrors lastTlsErrors;
        private string lastTlsReason;

        public SupabaseTarget(string connectionString, string caCertificatePath)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Falta la cadena de conexión de Supabase.");
            NpgsqlConnectionStringBuilder settings;
            try { settings = new NpgsqlConnectionStringBuilder(connectionString); }
            catch (Exception ex) { throw new InvalidOperationException("La cadena de conexión de Supabase no es válida: " + Mask(ex.Message, connectionString, null)); }
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Username))
                throw new InvalidOperationException("Configure el servidor y el usuario de Supabase.");
            if (string.IsNullOrWhiteSpace(caCertificatePath) || !File.Exists(caCertificatePath))
                throw new InvalidOperationException("No se encontró el certificado de la CA de Supabase (Assets\\supabase-ca.crt). Reinstale EdenPOS.");

            secret = settings.Password;
            hostLabel = settings.Host;
            settings.SslMode = SslMode.Require;          // la validación real la hace el callback de abajo
            settings.PersistSecurityInfo = false;
            settings.Timeout = 8;
            settings.CommandTimeout = 30;
            settings.Pooling = true;
            settings.MinPoolSize = 0;
            settings.MaxPoolSize = 2;
            settings.ConnectionIdleLifetime = 30;
            settings.ApplicationName = "EdenPOS-replica";
            settings.Remove("Root Certificate");
            settings.Remove("RootCertificate");

            byte[] rootBytes;
            try { using (var certificate = new X509Certificate2(caCertificatePath)) rootBytes = certificate.RawData; }
            catch (Exception ex) { throw new InvalidOperationException("El certificado de la CA de Supabase no se pudo leer: " + Mask(ex.Message, connectionString, null)); }

            var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
            // .NET Framework no permite asignar un almacén de confianza propio a SslStream:
            // se exige que no haya otro error que el de cadena (p. ej. nombre de host distinto)
            // y que la cadena construida termine exactamente en la CA de Supabase.
            builder.UseUserCertificateValidationCallback((sender, certificate, providedChain, errors) =>
            {
                string reason;
                var ok = ValidateServerCertificate(rootBytes, certificate, providedChain, errors, out reason);
                lock (cacheLock) { lastTlsErrors = errors; lastTlsReason = ok ? null : reason; }
                return ok;
            });
            dataSource = builder.Build();
        }

        internal static bool ValidateServerCertificate(byte[] rootBytes, X509Certificate certificate, X509Chain providedChain, SslPolicyErrors errors)
        {
            string reason;
            return ValidateServerCertificate(rootBytes, certificate, providedChain, errors, out reason);
        }

        // reason: motivo del rechazo ("El certificado no es de la CA indicada", "El nombre del servidor no coincide con el certificado"...).
        internal static bool ValidateServerCertificate(byte[] rootBytes, X509Certificate certificate, X509Chain providedChain, SslPolicyErrors errors, out string reason)
        {
            reason = null;
            if (certificate == null || providedChain == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != SslPolicyErrors.None)
            {
                reason = "El servidor no presentó un certificado TLS.";
                return false;
            }
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None)
            {
                reason = "El nombre del servidor no coincide con el certificado.";
                return false;
            }
            if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                reason = "El certificado del servidor no es válido (" + errors + ").";
                return false;
            }
            try
            {
                using (var root = new X509Certificate2(rootBytes))
                using (var leaf = new X509Certificate2(certificate))
                using (var chain = new X509Chain())
                {
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                    chain.ChainPolicy.ExtraStore.Add(root);
                    foreach (X509ChainElement element in providedChain.ChainElements)
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    if (!chain.Build(leaf)) { reason = "El certificado no es de la CA indicada."; return false; }
                    var anchor = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                    if (anchor.RawData.SequenceEqual(rootBytes)) return true;
                    reason = "El certificado no es de la CA indicada.";
                    return false;
                }
            }
            catch
            {
                reason = "El certificado del servidor no se pudo verificar.";
                return false;
            }
        }

        public void EnsureReady()
        {
            Run(() =>
            {
                using (var connection = OpenConnection())
                {
                    var found = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    using (var command = new NpgsqlCommand(
                        "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = @schema", connection))
                    {
                        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Varchar) { Value = SchemaName });
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                            {
                                var table = reader.GetString(0);
                                Dictionary<string, string> columns;
                                if (!found.TryGetValue(table, out columns))
                                    found[table] = columns = new Dictionary<string, string>(StringComparer.Ordinal);
                                columns[reader.GetString(1)] = reader.GetString(2);
                            }
                    }
                    var missing = RequiredTables.Where(t => !found.ContainsKey(t)).ToArray();
                    if (missing.Length > 0)
                        throw new InvalidOperationException(MissingScriptMessage + " No se encontraron las tablas: " + string.Join(", ", missing) + ".");
                    lock (cacheLock) columnTypes = found;
                }
            });
        }

        public void Apply(ReplicaTable table, IList<object[]> upserts, IList<object[]> deleteKeys)
        {
            ValidateTable(table);
            var hasUpserts = upserts != null && upserts.Count > 0;
            var hasDeletes = deleteKeys != null && deleteKeys.Count > 0;
            if (!hasUpserts && !hasDeletes) return;
            var types = TypesFor(table);

            Run(() =>
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    if (hasDeletes)
                    {
                        var keyTypes = table.KeyColumns.Select(c => types[c]).ToArray();
                        for (var offset = 0; offset < deleteKeys.Count; offset += BatchSize)
                        {
                            var count = Math.Min(BatchSize, deleteKeys.Count - offset);
                            using (var command = new NpgsqlCommand(BuildDeleteSql(table, count), connection, transaction))
                            {
                                for (var row = 0; row < count; row++)
                                {
                                    var key = deleteKeys[offset + row];
                                    if (key == null || key.Length != table.KeyColumns.Length)
                                        throw new ArgumentException("La clave a borrar de " + table.Name + " no tiene " + table.KeyColumns.Length + " valores.");
                                    for (var i = 0; i < keyTypes.Length; i++)
                                        command.Parameters.Add(MakeParameter("k" + row + "_" + i, keyTypes[i], key[i]));
                                }
                                command.ExecuteNonQuery();
                            }
                        }
                    }
                    if (hasUpserts)
                    {
                        var columnTypeList = table.Columns.Select(c => types[c]).ToArray();
                        var keyIndexes = table.KeyColumns.Select(k => Array.IndexOf(table.Columns, k)).ToArray();
                        // Una sentencia con la misma clave dos veces falla en ON CONFLICT: se conserva la última fila.
                        var unique = new Dictionary<string, object[]>(StringComparer.Ordinal);
                        var order = new List<string>();
                        foreach (var row in upserts)
                        {
                            if (row == null || row.Length != table.Columns.Length)
                                throw new ArgumentException("Una fila de " + table.Name + " no tiene " + table.Columns.Length + " valores.");
                            var id = string.Join("\u0001", keyIndexes.Select(i => Convert.ToString(Coerce(columnTypeList[i], row[i]), CultureInfo.InvariantCulture)));
                            if (!unique.ContainsKey(id)) order.Add(id);
                            unique[id] = row;
                        }
                        for (var offset = 0; offset < order.Count; offset += BatchSize)
                        {
                            var count = Math.Min(BatchSize, order.Count - offset);
                            using (var command = new NpgsqlCommand(BuildUpsertSql(table, count), connection, transaction))
                            {
                                for (var r = 0; r < count; r++)
                                {
                                    var row = unique[order[offset + r]];
                                    for (var c = 0; c < columnTypeList.Length; c++)
                                        command.Parameters.Add(MakeParameter("p" + r + "_" + c, columnTypeList[c], row[c]));
                                }
                                command.ExecuteNonQuery();
                            }
                        }
                    }
                    transaction.Commit();
                }
            });
        }

        public IEnumerable<object[]> ReadAll(ReplicaTable table)
        {
            ValidateTable(table);
            var types = TypesFor(table);
            var sql = BuildSelectSql(table);
            var rows = new List<object[]>();
            // Se lee completo (una réplica de un POS es pequeña) para no dejar la conexión abierta mientras el llamador escribe en MariaDB.
            Run(() =>
            {
                using (var connection = OpenConnection())
                using (var command = new NpgsqlCommand(sql, connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var values = new object[table.Columns.Length];
                        for (var i = 0; i < values.Length; i++)
                            values[i] = reader.IsDBNull(i) ? DBNull.Value : FromPostgres(types[table.Columns[i]], reader.GetValue(i));
                        rows.Add(values);
                    }
            });
            return rows;
        }

        public void SetInfo(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("La clave de información de la réplica no puede estar vacía.");
            Run(() =>
            {
                using (var connection = OpenConnection())
                using (var command = new NpgsqlCommand(
                    "INSERT INTO " + SchemaName + "._replica_info (clave, valor) VALUES (@k, @v) ON CONFLICT (clave) DO UPDATE SET valor = EXCLUDED.valor", connection))
                {
                    command.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = key });
                    command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object)value ?? DBNull.Value });
                    command.ExecuteNonQuery();
                }
            });
        }

        public ICollection<string> GetColumnNames(string table)
        {
            Dictionary<string, Dictionary<string, string>> cache;
            lock (cacheLock) cache = columnTypes;
            if (cache == null)
                throw new InvalidOperationException("Llame a EnsureReady antes de usar la réplica de Supabase.");
            Dictionary<string, string> columns;
            return table != null && cache.TryGetValue(table, out columns) ? new HashSet<string>(columns.Keys, StringComparer.Ordinal) : null;
        }

        public string GetInfo(string key)
        {
            string result = null;
            Run(() =>
            {
                using (var connection = OpenConnection())
                using (var command = new NpgsqlCommand("SELECT valor FROM " + SchemaName + "._replica_info WHERE clave = @k", connection))
                {
                    command.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = key ?? "" });
                    var value = command.ExecuteScalar();
                    result = value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            });
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            dataSource.Dispose();
        }

        // ---------- SQL (internal para poder probarlo) ----------

        internal static string Quote(string identifier)
        {
            if (identifier == null || !IdentifierPattern.IsMatch(identifier))
                throw new ArgumentException("Nombre de tabla o columna no válido para la réplica: " + identifier);
            return "\"" + identifier + "\"";
        }

        internal static string QualifiedName(ReplicaTable table)
        {
            return SchemaName + "." + Quote(table.Name);
        }

        internal static string BuildUpsertSql(ReplicaTable table, int rowCount)
        {
            ValidateTable(table);
            if (rowCount < 1) throw new ArgumentOutOfRangeException("rowCount");
            var sql = new StringBuilder();
            sql.Append("INSERT INTO ").Append(QualifiedName(table)).Append(" (")
               .Append(string.Join(", ", table.Columns.Select(Quote))).Append(") VALUES ");
            for (var r = 0; r < rowCount; r++)
            {
                if (r > 0) sql.Append(", ");
                sql.Append('(');
                for (var c = 0; c < table.Columns.Length; c++)
                {
                    if (c > 0) sql.Append(", ");
                    sql.Append("@p").Append(r).Append('_').Append(c);
                }
                sql.Append(')');
            }
            sql.Append(" ON CONFLICT (").Append(string.Join(", ", table.KeyColumns.Select(Quote))).Append(") ");
            var updatable = table.Columns.Where(c => !table.KeyColumns.Contains(c)).ToArray();
            if (updatable.Length == 0) sql.Append("DO NOTHING");
            else sql.Append("DO UPDATE SET ").Append(string.Join(", ", updatable.Select(c => Quote(c) + " = EXCLUDED." + Quote(c))));
            return sql.ToString();
        }

        internal static string BuildDeleteSql(ReplicaTable table, int rowCount)
        {
            ValidateTable(table);
            if (rowCount < 1) throw new ArgumentOutOfRangeException("rowCount");
            var sql = new StringBuilder("DELETE FROM " + QualifiedName(table) + " WHERE ");
            for (var r = 0; r < rowCount; r++)
            {
                if (r > 0) sql.Append(" OR ");
                sql.Append('(');
                for (var i = 0; i < table.KeyColumns.Length; i++)
                {
                    if (i > 0) sql.Append(" AND ");
                    sql.Append(Quote(table.KeyColumns[i])).Append(" = @k").Append(r).Append('_').Append(i);
                }
                sql.Append(')');
            }
            return sql.ToString();
        }

        internal static string BuildSelectSql(ReplicaTable table)
        {
            ValidateTable(table);
            return "SELECT " + string.Join(", ", table.Columns.Select(Quote)) + " FROM " + QualifiedName(table) +
                   " ORDER BY " + string.Join(", ", table.KeyColumns.Select(Quote));
        }

        private static void ValidateTable(ReplicaTable table)
        {
            if (table == null) throw new ArgumentNullException("table");
            Quote(table.Name);
            if (table.Columns == null || table.Columns.Length == 0) throw new ArgumentException("La tabla " + table.Name + " no tiene columnas.");
            if (table.KeyColumns == null || table.KeyColumns.Length == 0) throw new ArgumentException("La tabla " + table.Name + " no tiene clave primaria.");
            foreach (var column in table.Columns) Quote(column);
            foreach (var key in table.KeyColumns)
                if (Array.IndexOf(table.Columns, key) < 0) throw new ArgumentException("La clave " + key + " no está entre las columnas de " + table.Name + ".");
        }

        // ---------- Conversión de tipos ----------

        internal static NpgsqlParameter MakeParameter(string name, string pgType, object value)
        {
            return new NpgsqlParameter(name, DbTypeFor(pgType)) { Value = Coerce(pgType, value) };
        }

        internal static NpgsqlDbType DbTypeFor(string pgType)
        {
            switch (pgType)
            {
                case "integer": return NpgsqlDbType.Integer;
                case "smallint": return NpgsqlDbType.Smallint;
                case "bigint": return NpgsqlDbType.Bigint;
                case "numeric": return NpgsqlDbType.Numeric;
                case "boolean": return NpgsqlDbType.Boolean;
                case "timestamp without time zone": return NpgsqlDbType.Timestamp;
                case "text": return NpgsqlDbType.Text;
                case "character varying": return NpgsqlDbType.Varchar;
                case "character": return NpgsqlDbType.Varchar;
                default: throw new InvalidOperationException("Tipo de columna de Supabase no soportado por la réplica: " + pgType + ".");
            }
        }

        // Convierte un valor entregado por MySqlConnector (bool/sbyte/byte/short/int/long/decimal/DateTime/string) al tipo de la columna.
        internal static object Coerce(string pgType, object value)
        {
            if (value == null || value is DBNull) return DBNull.Value;
            var inv = CultureInfo.InvariantCulture;
            switch (pgType)
            {
                case "boolean":
                    if (value is bool) return value;
                    if (value is string)
                    {
                        var s = ((string)value).Trim();
                        if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                        throw new InvalidCastException("Valor booleano no válido: " + s);
                    }
                    return Convert.ToDecimal(value, inv) != 0m;
                case "integer":
                    return value is bool ? ((bool)value ? 1 : 0) : Convert.ToInt32(value, inv);
                case "smallint":
                    return value is bool ? (short)((bool)value ? 1 : 0) : Convert.ToInt16(value, inv);
                case "bigint":
                    return value is bool ? ((bool)value ? 1L : 0L) : Convert.ToInt64(value, inv);
                case "numeric":
                    return value is decimal ? value : Convert.ToDecimal(value is IConvertible ? value : value.ToString(), inv);
                case "timestamp without time zone":
                    {
                        var date = value is DateTime ? (DateTime)value : Convert.ToDateTime(value, inv);
                        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    }
                case "text":
                case "character varying":
                case "character":
                    if (value is string) return value;
                    if (value is bool) return (bool)value ? "1" : "0";
                    return Convert.ToString(value, inv);
                default:
                    throw new InvalidOperationException("Tipo de columna de Supabase no soportado por la réplica: " + pgType + ".");
            }
        }

        // Devuelve tipos que MySqlConnector acepta al insertar en MariaDB.
        internal static object FromPostgres(string pgType, object value)
        {
            if (value == null || value is DBNull) return DBNull.Value;
            switch (pgType)
            {
                case "boolean": return value is bool ? value : Convert.ToBoolean(value);
                case "integer": return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                case "smallint": return Convert.ToInt16(value, CultureInfo.InvariantCulture);
                case "bigint": return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                case "numeric": return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                case "timestamp without time zone": return DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        // ---------- Conexión y errores ----------

        private Dictionary<string, string> TypesFor(ReplicaTable table)
        {
            Dictionary<string, Dictionary<string, string>> cache;
            lock (cacheLock) cache = columnTypes;
            if (cache == null)
                throw new InvalidOperationException("Llame a EnsureReady antes de usar la réplica de Supabase.");
            Dictionary<string, string> columns;
            if (!cache.TryGetValue(table.Name, out columns))
                throw new InvalidOperationException(MissingScriptMessage + " Falta la tabla " + table.Name + ".");
            var missing = table.Columns.Where(c => !columns.ContainsKey(c)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("La tabla " + table.Name + " de Supabase no tiene las columnas " + string.Join(", ", missing) +
                                                    ". Vuelva a ejecutar el script de Supabase actualizado.");
            return columns;
        }

        private NpgsqlConnection OpenConnection()
        {
            if (disposed) throw new ObjectDisposedException("SupabaseTarget");
            return dataSource.OpenConnection();
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { throw; }
            catch (ArgumentException) { throw; }
            catch (Exception ex) { throw Translate(ex); }
        }

        internal Exception Translate(Exception ex)
        {
            var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
            var inner = Innermost(ex);
            string message;
            if (pg != null) message = DescribePostgres(pg);
            else if (HasTenantNotFound(ex))
                message = TenantNotFoundMessage;
            else if (ex is System.Security.Authentication.AuthenticationException || inner is System.Security.Authentication.AuthenticationException)
            {
                string reason;
                lock (cacheLock) reason = lastTlsReason;
                message = "No se pudo verificar el certificado TLS de Supabase (" + hostLabel + ")." +
                          (string.IsNullOrEmpty(reason) ? " Compruebe el servidor configurado." : " " + reason);
            }
            else if (ex is TimeoutException || inner is TimeoutException)
                message = "Se agotó el tiempo de espera con Supabase (" + hostLabel + "). Revise la conexión a Internet.";
            else if (ex is System.Net.Sockets.SocketException || ex is IOException || inner is System.Net.Sockets.SocketException || inner is IOException)
                message = "No se pudo conectar con Supabase (" + hostLabel + "): " + Mask(inner.Message, secret, null);
            else if (ex is NpgsqlException)
                message = "Error de comunicación con Supabase (" + hostLabel + "): " + Mask(inner.Message, secret, null);
            else
                message = "Error en la réplica de Supabase: " + Mask(ex.Message, secret, null);
            // Sin la excepción original como InnerException para no filtrar texto sin enmascarar.
            return new InvalidOperationException(message);
        }

        internal SslPolicyErrors LastTlsErrors { get { lock (cacheLock) return lastTlsErrors; } }

        private const string TenantNotFoundMessage = "Supabase no reconoce el usuario (Tenant or user not found). Con el Session pooler el usuario debe tener el formato edenpos_replica.<referencia-del-proyecto>.";

        private static bool HasTenantNotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var text = e is PostgresException ? ((PostgresException)e).MessageText : e.Message;
                if (text != null && text.IndexOf("Tenant or user not found", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Traduce un error devuelto por PostgreSQL según su clase SQLSTATE; siempre incluye el código.
        private string DescribePostgres(PostgresException pg)
        {
            var state = pg.SqlState ?? "";
            var detail = Mask(pg.MessageText ?? pg.Message, secret, null);
            var tag = " (código " + (state.Length == 0 ? "?" : state) + ")";
            if (HasTenantNotFound(pg)) return TenantNotFoundMessage + tag;
            if (state == "42P01" || state == "3F000") return MissingScriptMessage + tag;
            if (state == "42501") return "El usuario de Supabase no tiene permisos sobre el esquema edenpos. " + MissingScriptMessage + tag;
            if (state == "57014") return "Supabase canceló la consulta por tardar demasiado (tiempo de consulta agotado)." + tag;
            if (state == "53300") return "Supabase tiene demasiadas conexiones abiertas. Espere un momento y reintente; cierre otras PC o herramientas que usen la réplica." + tag;
            if (state.StartsWith("28", StringComparison.Ordinal)) return "Supabase rechazó el usuario o la contraseña. Revise la configuración de la réplica." + tag;
            if (state.StartsWith("22", StringComparison.Ordinal)) return "Supabase rechazó un dato por no ser válido para la columna: " + detail + tag;
            if (state.StartsWith("23", StringComparison.Ordinal)) return "Supabase rechazó un dato por violar una restricción de la tabla: " + detail + tag;
            return "Supabase devolvió un error: " + detail + tag;
        }

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        // Quita la contraseña (y la cadena de conexión completa) de cualquier texto que vaya a mostrarse o registrarse.
        internal static string Mask(string text, string secretOrConnection, string unused)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(secretOrConnection) && secretOrConnection.Length >= 3)
                text = text.Replace(secretOrConnection, "***");
            return Regex.Replace(text, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");
        }
    }
}
