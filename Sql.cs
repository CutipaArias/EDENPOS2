using System;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace EdenPOS
{
    public static class SqlConnectionFactory
    {
        public const string DefaultConnectionString = "Server=127.0.0.1;Port=3306;Database=edenpos;User ID=root;Password=;";
        public const string StrictSqlMode = "STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION";
        private const string EnvironmentVariable = "EDENPOS_MYSQL_CONNECTION";
        private static volatile string cachedConnectionString;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EdenPOS-MYSQL-v1");

        // La cadena de conexión se lee siempre del archivo local de ajustes (settings.json),
        // nunca de la base de datos a la que da acceso.
        public static string ResolveConnectionString()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return HardenConnectionString(fromEnvironment.Trim());
            // Se cachea la cadena resuelta (leer settings.json y descifrar con DPAPI en cada Open() es caro).
            var cached = cachedConnectionString;
            if (cached != null) return cached;
            var stored = Unprotect(AppData.LocalMySqlConnectionStringProtected);
            cached = HardenConnectionString(string.IsNullOrWhiteSpace(stored) ? DefaultConnectionString : stored);
            cachedConnectionString = cached;
            return cached;
        }

        // Se llama al guardar una cadena nueva en settings.json.
        public static void InvalidateCache()
        {
            cachedConnectionString = null;
        }

        private static string HardenConnectionString(string value)
        {
            var builder = new MySqlConnectionStringBuilder(value)
            {
                ConnectionReset = true,
                IgnoreCommandTransaction = false,
                PersistSecurityInfo = false,
                AllowLoadLocalInfile = false
            };
            return builder.ConnectionString;
        }

        public static MySqlConnection Open()
        {
            var connection = new MySqlConnection(ResolveConnectionString());
            try
            {
                connection.Open();
                // El MariaDB de XAMPP no viene en modo estricto (recorta datos y avisa en vez de fallar); varias protecciones
                // de la app cuentan con el error. Se fuerza el modo estricto en cada conexión (válido en MySQL 8.4 y MariaDB 10.4).
                using (var mode = new MySqlCommand("SET SESSION sql_mode = '" + StrictSqlMode + "'", connection))
                    mode.ExecuteNonQuery();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public static bool TryPing(out string error)
        {
            try
            {
                using (var connection = Open())
                using (var command = new MySqlCommand("SELECT 1", connection))
                {
                    command.ExecuteScalar();
                }
                error = "";
                return true;
            }
            catch (MySqlException ex)
            {
                error = DescribeMySqlError(ex);
                return false;
            }
            catch (ArgumentException)
            {
                error = "La cadena de conexión a MySQL no es válida.";
                return false;
            }
            catch (Exception ex)
            {
                error = "No se pudo conectar con la base de datos: " + ex.Message;
                return false;
            }
        }

        // true si el fallo es de conexión o de acceso al servidor (apagado, red, usuario, base inexistente, caída en medio de una consulta).
        // El resto de errores de MySQL (dato demasiado largo, llave foránea, tabla que falta, bloqueo) no son de conexión.
        public static bool IsConnectionError(MySqlException ex)
        {
            if (ex == null) return false;
            switch (ex.ErrorCode)
            {
                case MySqlErrorCode.UnableToConnectToHost:
                case MySqlErrorCode.AccessDenied:
                case MySqlErrorCode.DatabaseAccessDenied:
                case MySqlErrorCode.UnknownDatabase:
                case MySqlErrorCode.ConnectionCountError:
                case MySqlErrorCode.TooManyUserConnections:
                case MySqlErrorCode.HandshakeError:
                case MySqlErrorCode.HostIsBlocked:
                case MySqlErrorCode.HostNotPrivileged:
                case MySqlErrorCode.ServerShutdown:
                case MySqlErrorCode.AbortingConnection:
                case MySqlErrorCode.NewAbortingConnection:
                case MySqlErrorCode.NetReadError:
                case MySqlErrorCode.NetReadInterrupted:
                case MySqlErrorCode.NetErrorOnWrite:
                case MySqlErrorCode.NetWriteInterrupted:
                case MySqlErrorCode.NetPacketsOutOfOrder:
                    return true;
            }
            // Conexión perdida en medio de una consulta: MySqlConnector envuelve la IOException/SocketException de red.
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                if (inner is System.IO.IOException || inner is System.Net.Sockets.SocketException) return true;
            return false;
        }

        private static string DescribeMySqlError(MySqlException ex)
        {
            switch (ex.ErrorCode)
            {
                case MySqlErrorCode.UnableToConnectToHost:
                    return "No se pudo conectar con el servidor MySQL. Verifique que esté encendido y que la dirección y el puerto sean correctos.";
                case MySqlErrorCode.AccessDenied:
                    return "MySQL rechazó el usuario o la contraseña.";
                case MySqlErrorCode.UnknownDatabase:
                    return "La base de datos indicada no existe en el servidor MySQL.";
                case MySqlErrorCode.ConnectionCountError:
                    return "El servidor MySQL tiene demasiadas conexiones abiertas. Intente de nuevo en unos segundos.";
                default:
                    return "Error de MySQL: " + ex.Message;
            }
        }

        private static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return "";
            try
            {
                var clearBytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException)
            {
                throw new InvalidOperationException("No se pudo descifrar la configuración de MySQL. Configure de nuevo la conexión con esta cuenta de Windows.", ex);
            }
        }
    }
}
