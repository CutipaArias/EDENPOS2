using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MySqlConnector;

namespace EdenPOS
{
    public static partial class AppData
    {
        private static readonly object Sync = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static volatile string currencySymbolCache;

        public static string DataDirectory
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("EDENPOS_DATA_PATH");
                if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
                var pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data.path");
                if (File.Exists(pathFile))
                {
                    var configuredPath = File.ReadAllText(pathFile).Trim();
                    if (!string.IsNullOrWhiteSpace(configuredPath))
                        return Path.GetFullPath(Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));
                }
                if (IsPortableMode)
                {
                    var visibleDatabase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BaseDatos");
                    var legacyPortable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                    if (!Directory.Exists(visibleDatabase) && Directory.Exists(legacyPortable)) return legacyPortable;
                    return visibleDatabase;
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdenPOS", "Data");
            }
        }

        public static bool IsPortableMode
        {
            get { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.mode")); }
        }

        public static string AssetDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Catalog"); }
        }

        public static event EventHandler DataChanged;

        // Texto para el usuario ante un MySqlException: el aviso de "MySQL apagado" solo si de verdad es un fallo de conexión o acceso.
        public static string DescribeDatabaseError(MySqlException ex)
        {
            return SqlConnectionFactory.IsConnectionError(ex)
                ? "No hay conexión con la base de datos. Verifique que MySQL esté encendido e intente de nuevo."
                : "Ocurrió un error en la base de datos. El detalle quedó registrado en errors.log.";
        }

        public static void Initialize()
        {
            Directory.CreateDirectory(DataDirectory);
            // settings.json ya no se crea: solo guarda datos locales (cadena de conexión) y es opcional.
            // Los recibos viven en MySQL (recibo / recibo_linea); ya no se crea receipts.json.
            SeedProductsIfEmpty();
            MigrateDefaults();
        }

        public static AppSettings Settings
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                {
                    var settings = ReadConfiguration(connection, null);
                    var local = LoadLocalSettings();
                    settings.MySqlConnectionStringProtected = local.MySqlConnectionStringProtected ?? "";
                    // El token de DNI (cifrado con DPAPI, ligado a este usuario de Windows) vive solo en el settings.json local.
                    settings.DniApiTokenProtected = local.DniApiTokenProtected ?? "";
                    settings.InventorySchemaVersion = local.InventorySchemaVersion;
                    settings.CatalogSchemaVersion = local.CatalogSchemaVersion;
                    settings.SecuritySchemaVersion = local.SecuritySchemaVersion;
                    settings.Users = LoadUsersFromDb(connection, null);
                    currencySymbolCache = settings.CurrencySymbol;
                    return settings;
                }
            }
        }

        public static List<Product> Products
        {
            get
            {
                lock (Sync)
                {
                    // La huella se calcula en la misma lectura (misma conexión) que devuelve la lista: SaveProducts la compara con el servidor.
                    using (var connection = SqlConnectionFactory.Open())
                    {
                        // Huella primero: si el catálogo cambia entre las dos lecturas, la huella queda más vieja que la lista (falso conflicto seguro, nunca al revés).
                        var fingerprint = ComputeCatalogFingerprint(connection, null, false);
                        var list = LoadProductsFromDb(connection, null, false);
                        catalogFingerprint = fingerprint;
                        return list;
                    }
                }
            }
        }

        // Huella del catálogo (definición, sin stock) vista por este proceso en su última lectura de Products. null = aún no leída.
        private static string catalogFingerprint;

        // SHA-256 de la definición del catálogo: ids, categoría, nombre, precio, imagen, color, activo, stock mínimo y variantes (nombre, orden,
        // precio, stock mínimo). NO incluye stock, stock inicial ni controla_inventario: ventas y entradas de inventario (que cambian solo eso)
        // no deben provocar falsos conflictos. Con lockRows bloquea las filas leídas (FOR UPDATE) hasta el fin de la transacción.
        private static string ComputeCatalogFingerprint(MySqlConnection connection, MySqlTransaction transaction, bool lockRows)
        {
            var lockClause = lockRows ? " FOR UPDATE" : "";
            var text = new StringBuilder();
            using (var command = new MySqlCommand(
                "SELECT id_producto, categoria, nombre, precio, imagen, color_hex, activo, stock_minimo FROM producto ORDER BY id_producto" + lockClause, connection, transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    text.Append("P|").Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetString(2)).Append('|')
                        .Append(reader.GetDecimal(3).ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
                        .Append(reader.IsDBNull(4) ? "" : reader.GetString(4)).Append('|').Append(reader.GetString(5)).Append('|')
                        .Append(reader.GetBoolean(6) ? "1" : "0").Append('|').Append(reader.GetInt32(7).ToString(CultureInfo.InvariantCulture)).Append('\n');
            using (var command = new MySqlCommand(
                "SELECT id_variante, id_producto, nombre, orden, precio, stock_minimo FROM producto_variante ORDER BY id_variante" + lockClause, connection, transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    text.Append("V|").Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetString(2)).Append('|')
                        .Append(reader.GetInt32(3).ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(reader.GetDecimal(4).ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
                        .Append(reader.GetInt32(5).ToString(CultureInfo.InvariantCulture)).Append('\n');
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        public static List<Receipt> Receipts
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return ReadReceipts(connection, null, null);
            }
        }

        public static List<SavedTicket> SavedTickets
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return LoadSavedTicketsFromDb(connection, null, null);
            }
        }

        public static List<Customer> Customers
        {
            get { lock (Sync) return LoadCustomersFromDb(); }
        }

        public static List<AppUser> Users
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return LoadUsersFromDb(connection, null);
            }
        }

        public static List<Shift> Shifts
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return ReadShifts(connection, null, "ORDER BY numero, fecha_apertura", new Dictionary<string, object>());
            }
        }

        public static List<CashMovement> CashMovements
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                using (var command = new MySqlCommand(
                    "SELECT id_movimiento, id_sesion, created_at, empleado, tipo, importe, comentario FROM movimiento_caja ORDER BY created_at, id_movimiento", connection))
                using (var reader = command.ExecuteReader())
                {
                    var movements = new List<CashMovement>();
                    while (reader.Read())
                    {
                        movements.Add(new CashMovement
                        {
                            Id = reader.GetString(0),
                            ShiftId = reader.GetString(1),
                            CreatedAt = reader.GetDateTime(2),
                            Employee = reader.GetString(3),
                            Type = reader.GetString(4),
                            Amount = reader.GetDecimal(5),
                            Comment = reader.GetString(6)
                        });
                    }
                    return movements;
                }
            }
        }

        public static List<InventoryMovement> InventoryMovements
        {
            get { lock (Sync) return LoadInventoryMovementsFromDb(); }
        }

        public static Shift OpenShift
        {
            get
            {
                lock (Sync)
                using (var connection = SqlConnectionFactory.Open())
                    return ReadOpenShift(connection, null);
            }
        }

        public static void SaveSettings(AppSettings value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.PromoGrupalMinimoPersonas < PromoMinPersonas || value.PromoGrupalMinimoPersonas > PromoMaxPersonas)
                throw new ArgumentException("El mínimo de personas para el descuento grupal debe estar entre " + PromoMinPersonas + " y " + PromoMaxPersonas + ".");
            if (value.PromoGrupalPorcentaje < 0m || value.PromoGrupalPorcentaje > PromoMaxPorcentaje)
                throw new ArgumentException("El porcentaje del descuento grupal debe estar entre 0 y " + PromoMaxPorcentaje + ".");
            var token = value.DniApiTokenProtected ?? "";
            lock (Sync)
            {
                // Los campos de negocio y las promociones (promo_*) van a MySQL. Los contadores next_receipt_number y
                // next_shift_number solo los avanzan AddReceipt y OpenNewShift dentro de sus transacciones: el UPDATE no los escribe
                // (el INSERT inicial de la fila sí los necesita).
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction,
                        "INSERT INTO configuracion (id, store_name, employee_name, currency_symbol, next_receipt_number, next_shift_number, dni_lookup_enabled, " +
                        "promo_sexta_visita_activa, promo_grupal_minimo_personas, promo_grupal_porcentaje) " +
                        "VALUES (1, @tienda, @empleado, @moneda, @recibo, @turno, @dni, @sexta, @minimo, @porcentaje) " +
                        "ON DUPLICATE KEY UPDATE store_name = VALUES(store_name), employee_name = VALUES(employee_name), currency_symbol = VALUES(currency_symbol), " +
                        "dni_lookup_enabled = VALUES(dni_lookup_enabled), promo_sexta_visita_activa = VALUES(promo_sexta_visita_activa), " +
                        "promo_grupal_minimo_personas = VALUES(promo_grupal_minimo_personas), promo_grupal_porcentaje = VALUES(promo_grupal_porcentaje)",
                        new Dictionary<string, object>
                        {
                            { "@tienda", Clip(value.StoreName, 80) },
                            { "@empleado", Clip(value.EmployeeName, 80) },
                            { "@moneda", Clip(value.CurrencySymbol, 5) },
                            { "@recibo", Math.Max(1, value.NextReceiptNumber) },
                            { "@turno", Math.Max(1, value.NextShiftNumber) },
                            { "@dni", value.DniLookupEnabled },
                            { "@sexta", value.PromoSextaVisitaActiva },
                            { "@minimo", value.PromoGrupalMinimoPersonas },
                            { "@porcentaje", Math.Round(value.PromoGrupalPorcentaje, 2) }
                        });
                    transaction.Commit();
                }
                currencySymbolCache = Clip(value.CurrencySymbol, 5);
                // La cadena de conexión y el token de DNI nunca van a la base de datos: solo al settings.json local, y solo si cambiaron.
                var local = LoadLocalSettings();
                var newConnection = value.MySqlConnectionStringProtected;
                var connectionChanged = newConnection != null && newConnection != (local.MySqlConnectionStringProtected ?? "");
                var tokenChanged = token != (local.DniApiTokenProtected ?? "");
                if (connectionChanged || tokenChanged)
                    SaveLocalValues(connectionChanged ? newConnection : null, tokenChanged ? token : null);
                RaiseChanged();
            }
        }

        public static AppUser AuthenticateUser(string userId, string pin)
        {
            var user = Users.FirstOrDefault(x => x.Id == userId && x.Active);
            return UserSecurity.VerifyPin(user, pin) ? user : null;
        }

        public static AppUser SaveUser(string id, string name, string pin, string role)
        {
            lock (Sync)
            {
                if (!Validation.IsValidRequiredText(name, 3, 60))
                    throw new ArgumentException("El nombre debe tener entre 3 y 60 caracteres.");
                if (!UserRoles.IsValid(role))
                    throw new ArgumentException("Seleccione un rol válido.");

                var normalizedName = Validation.NormalizeName(name);
                AppUser user;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var users = LoadUsersFromDb(connection, transaction);
                    if (users.Any(x => x.Id != id && string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Ya existe un usuario con ese nombre.");

                    user = users.FirstOrDefault(x => x.Id == id);
                    if (user == null)
                    {
                        user = UserSecurity.CreateUser(normalizedName, pin, role);
                        user.Id = InsertUser(connection, transaction, user).ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        if (AppSession.CurrentUser != null && AppSession.CurrentUser.Id == user.Id && user.Role != role)
                            throw new InvalidOperationException("No puede cambiar su propio rol durante la sesión actual.");
                        if (user.Role == UserRoles.Owner && role != UserRoles.Owner &&
                            users.Count(x => x.Active && x.Role == UserRoles.Owner) <= 1)
                            throw new InvalidOperationException("Debe existir al menos una dueña activa.");
                        if (!string.IsNullOrWhiteSpace(pin)) UserSecurity.SetPin(user, pin);
                        else if (string.IsNullOrWhiteSpace(user.PinHash))
                            throw new ArgumentException("Ingrese un PIN de 4 a 8 dígitos.");
                        user.Name = normalizedName;
                        user.Role = role;
                        user.UpdatedAt = DateTime.Now;
                        Execute(connection, transaction,
                            "UPDATE usuario SET id_rol = @rol, nombre = @nombre, pin_hash = @hash, pin_salt = @salt, updated_at = @actualizado WHERE id_usuario = @id",
                            new Dictionary<string, object>
                            {
                                { "@id", int.Parse(user.Id, CultureInfo.InvariantCulture) },
                                { "@rol", ResolveRoleId(connection, transaction, role) },
                                { "@nombre", Clip(user.Name, 60) },
                                { "@hash", user.PinHash },
                                { "@salt", user.PinSalt },
                                { "@actualizado", user.UpdatedAt }
                            });
                    }
                    transaction.Commit();
                }
                if (AppSession.CurrentUser != null && AppSession.CurrentUser.Id == user.Id) AppSession.SignIn(user);
                RaiseChanged();
                return user;
            }
        }

        public static void SetUserActive(string id, bool active)
        {
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var users = LoadUsersFromDb(connection, transaction);
                    var user = users.FirstOrDefault(x => x.Id == id);
                    if (user == null) throw new InvalidOperationException("El usuario ya no existe.");
                    if (AppSession.CurrentUser != null && AppSession.CurrentUser.Id == id)
                        throw new InvalidOperationException("No puede desactivar al usuario que tiene la sesión abierta.");
                    if (!active && user.Role == UserRoles.Owner &&
                        users.Count(x => x.Active && x.Role == UserRoles.Owner) <= 1)
                        throw new InvalidOperationException("Debe existir al menos una dueña activa.");
                    Execute(connection, transaction, "UPDATE usuario SET activo = @activo, updated_at = @actualizado WHERE id_usuario = @id",
                        new Dictionary<string, object>
                        {
                            { "@id", int.Parse(user.Id, CultureInfo.InvariantCulture) },
                            { "@activo", active },
                            { "@actualizado", DateTime.Now }
                        });
                    if (!active)
                        RegistrarAuditoria(connection, transaction, AuditActions.UsuarioDesactivado, "Usuario " + user.Name + " (" + user.Role + ")");
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        public static void DeleteUser(string id)
        {
            lock (Sync)
            {
                try
                {
                    using (var connection = SqlConnectionFactory.Open())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var users = LoadUsersFromDb(connection, transaction);
                        var user = users.FirstOrDefault(x => x.Id == id);
                        if (user == null) return;
                        if (AppSession.CurrentUser != null && AppSession.CurrentUser.Id == id)
                            throw new InvalidOperationException("No puede eliminar al usuario que tiene la sesión abierta.");
                        if (user.Role == UserRoles.Owner && user.Active &&
                            users.Count(x => x.Active && x.Role == UserRoles.Owner) <= 1)
                            throw new InvalidOperationException("Debe existir al menos una dueña activa.");
                        // Los turnos y movimientos de caja conservan el nombre del empleado y pasan a id_usuario NULL (ON DELETE SET NULL).
                        Execute(connection, transaction, "DELETE FROM usuario WHERE id_usuario = @id",
                            new Dictionary<string, object> { { "@id", int.Parse(user.Id, CultureInfo.InvariantCulture) } });
                        RegistrarAuditoria(connection, transaction, AuditActions.UsuarioEliminado, "Usuario " + user.Name + " (" + user.Role + ")");
                        transaction.Commit();
                    }
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.RowIsReferenced2 || ex.ErrorCode == MySqlErrorCode.RowIsReferenced)
                {
                    throw new InvalidOperationException("No se puede eliminar al usuario porque tiene registros asociados (por ejemplo, de auditoría). Desactívelo en su lugar.", ex);
                }
                RaiseChanged();
            }
        }

        private static List<AppUser> LoadUsersFromDb(MySqlConnection connection, MySqlTransaction transaction)
        {
            var users = new List<AppUser>();
            using (var command = new MySqlCommand(
                "SELECT u.id_usuario, u.nombre, r.nombre, u.pin_salt, u.pin_hash, u.activo, u.created_at, u.updated_at " +
                "FROM usuario u INNER JOIN rol r ON r.id_rol = u.id_rol ORDER BY u.id_usuario", connection, transaction))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    users.Add(new AppUser
                    {
                        Id = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                        Name = reader.GetString(1),
                        Role = reader.GetString(2),
                        PinSalt = reader.GetString(3),
                        PinHash = reader.GetString(4),
                        Active = reader.GetBoolean(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    });
                }
            }
            return users;
        }

        // Inserta el usuario y devuelve el id_usuario generado por MySQL (el Id del modelo es ese número en texto).
        private static int InsertUser(MySqlConnection connection, MySqlTransaction transaction, AppUser user)
        {
            using (var command = new MySqlCommand(
                "INSERT INTO usuario (id_rol, nombre, pin_hash, pin_salt, activo, created_at, updated_at) " +
                "VALUES (@rol, @nombre, @hash, @salt, @activo, @creado, @actualizado)", connection, transaction))
            {
                command.Parameters.AddWithValue("@rol", ResolveRoleId(connection, transaction, user.Role));
                command.Parameters.AddWithValue("@nombre", Clip(user.Name, 60));
                command.Parameters.AddWithValue("@hash", user.PinHash);
                command.Parameters.AddWithValue("@salt", user.PinSalt);
                command.Parameters.AddWithValue("@activo", user.Active);
                command.Parameters.AddWithValue("@creado", user.CreatedAt);
                command.Parameters.AddWithValue("@actualizado", user.UpdatedAt);
                command.ExecuteNonQuery();
                return Convert.ToInt32(command.LastInsertedId);
            }
        }

        private static int ResolveRoleId(MySqlConnection connection, MySqlTransaction transaction, string role)
        {
            using (var command = new MySqlCommand("SELECT id_rol FROM rol WHERE nombre = @nombre", connection, transaction))
            {
                command.Parameters.AddWithValue("@nombre", role ?? "");
                var result = command.ExecuteScalar();
                if (result == null || result == DBNull.Value) throw new InvalidOperationException("El rol seleccionado no existe en la base de datos.");
                return Convert.ToInt32(result);
            }
        }

        // Lee la fila única de configuracion; si no existe devuelve los valores por defecto (sin insertarla).
        private static AppSettings ReadConfiguration(MySqlConnection connection, MySqlTransaction transaction)
        {
            var settings = CreateDefaultSettings();
            using (var command = new MySqlCommand(
                "SELECT store_name, employee_name, currency_symbol, next_receipt_number, next_shift_number, dni_lookup_enabled, " +
                "promo_sexta_visita_activa, promo_grupal_minimo_personas, promo_grupal_porcentaje " +
                "FROM configuracion WHERE id = 1", connection, transaction))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return settings;
                settings.StoreName = reader.GetString(0);
                settings.EmployeeName = reader.GetString(1);
                settings.CurrencySymbol = reader.GetString(2);
                settings.NextReceiptNumber = reader.GetInt32(3);
                settings.NextShiftNumber = reader.GetInt32(4);
                settings.DniLookupEnabled = reader.GetBoolean(5);
                settings.PromoSextaVisitaActiva = reader.GetBoolean(6);
                // Se acotan al leer: un valor fuera de rango en la base no debe romper el cobro ni impedir guardar la configuración.
                settings.PromoGrupalMinimoPersonas = Math.Min(PromoMaxPersonas, Math.Max(PromoMinPersonas, reader.GetInt32(7)));
                settings.PromoGrupalPorcentaje = Math.Min(PromoMaxPorcentaje, Math.Max(0m, reader.GetDecimal(8)));
            }
            return settings;
        }

        // Se ejecuta dentro de la transacción de la venta (AddReceipt), tras leer el contador con FOR UPDATE.
        private static void PersistNextReceiptNumber(MySqlConnection connection, MySqlTransaction transaction, int next)
        {
            Execute(connection, transaction, "UPDATE configuracion SET next_receipt_number = @siguiente WHERE id = 1",
                new Dictionary<string, object> { { "@siguiente", next } });
        }

        // settings.json local: cadena de conexión protegida, token de DNI protegido y versiones de esquema. Nunca consulta MySQL.
        private static AppSettings LoadLocalSettings()
        {
            return Load("settings", () => new AppSettings
            {
                MySqlConnectionStringProtected = "",
                DniApiTokenProtected = "",
                InventorySchemaVersion = 2,
                CatalogSchemaVersion = 2,
                SecuritySchemaVersion = 1
            });
        }

        // Lectura sin SQL para SqlConnectionFactory (evita recursión al averiguar cómo conectarse).
        internal static string LocalMySqlConnectionStringProtected
        {
            get { return LoadLocalSettings().MySqlConnectionStringProtected ?? ""; }
        }

        // Lee settings.json completo. Si no existe devuelve un diccionario vacío; si existe y no se puede leer o interpretar (bloqueado, corrupto),
        // lanza InvalidOperationException en vez de devolver vacío, para que nadie lo sobrescriba y pierda claves. Reintenta 3 veces por bloqueos transitorios.
        internal static Dictionary<string, object> ReadLocalSettingsStrict()
        {
            lock (Sync)
            {
                var path = PathFor("settings");
                Exception last = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(100);
                    try
                    {
                        if (!File.Exists(path)) return new Dictionary<string, object>();
                        var data = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                        if (data == null) throw new InvalidDataException("settings.json no contiene un objeto JSON.");
                        return data;
                    }
                    catch (Exception ex) { last = ex; }
                }
                LogError(last, "Leer la configuración local (settings.json)");
                throw new InvalidOperationException("No se pudo leer la configuración local; inténtelo de nuevo.", last);
            }
        }

        // Cambia solo las claves indicadas (null = no tocar) y conserva el resto del archivo existente (por ejemplo,
        // los datos antiguos que importa ImportLegacyJson). Invalida la cadena de conexión cacheada.
        private static void SaveLocalValues(string connectionStringProtected, string dniTokenProtected)
        {
            lock (Sync)
            {
                // Lectura estricta: si el archivo existe y no se puede leer, NO se escribe (se perderían la cadena de conexión y demás claves).
                var data = ReadLocalSettingsStrict();
                if (connectionStringProtected != null) data["MySqlConnectionStringProtected"] = connectionStringProtected;
                if (dniTokenProtected != null) data["DniApiTokenProtected"] = dniTokenProtected;
                Save("settings", data);
                SqlConnectionFactory.InvalidateCache();
            }
        }

        public static void SaveProducts(List<Product> value)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (Sync)
            {
                // Concurrencia optimista: solo escribe si el catálogo del servidor sigue igual al que este proceso leyó por última vez.
                WriteProducts(value, true);
                RaiseChanged();
            }
        }

        public static void AddInventoryEntry(string productId, int quantity, string note)
        {
            AddInventoryEntry(productId, null, quantity, note);
        }

        public static void AddInventoryEntry(string productId, string optionId, int quantity, string note)
        {
            if (quantity <= 0 || quantity > 1000000) throw new ArgumentException("La entrada debe ser mayor que cero.");
            lock (Sync)
            {
                // LoadProductsFromDb (no el getter Products): una entrada de stock no debe renovar la huella del catálogo de este proceso.
                var products = LoadProductsFromDb();
                var product = products.FirstOrDefault(x => x.Id == productId);
                if (product == null) throw new InvalidOperationException("El artículo ya no existe.");
                var changes = new List<StockChange>();
                if (!string.IsNullOrWhiteSpace(optionId))
                {
                    var option = (product.Options ?? new List<ProductOption>()).FirstOrDefault(x => x.Id == optionId);
                    if (option == null) throw new InvalidOperationException("La variante ya no existe.");
                    var previous = option.Stock;
                    option.TrackInventory = true;
                    option.Stock += quantity;
                    option.InitialStock += quantity;
                    changes.Add(new StockChange(product, option, NewInventoryMovement(product, option, "Entrada", quantity, previous, note, null)));
                }
                else
                {
                    var previous = product.Stock;
                    product.TrackInventory = true;
                    product.Stock += quantity;
                    product.InitialStock += quantity;
                    changes.Add(new StockChange(product, null, NewInventoryMovement(product, "Entrada", quantity, previous, note, null)));
                }
                PersistStockChanges(changes);
                RaiseChanged();
            }
        }

        public static void DeleteProduct(string productId)
        {
            lock (Sync)
            {
                var products = LoadProductsFromDb();
                var product = products.FirstOrDefault(x => x.Id == productId);
                if (product == null) return;
                string newFingerprint = null;
                bool advanceFingerprint = false;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    // Si la huella de este proceso coincidía con el servidor, el borrado propio no debe causar un falso conflicto después.
                    var before = ComputeCatalogFingerprint(connection, transaction, true);
                    advanceFingerprint = catalogFingerprint != null && catalogFingerprint == before;
                    // Comprobación en SQL (sin cargar todos los recibos): solo cuenta ventas del artículo, no de sus variantes.
                    using (var command = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM recibo_linea WHERE id_producto = @id)", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", productId);
                        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                            throw new InvalidOperationException("Este artículo ya tiene ventas registradas. Puede editarlo u ocultarlo, pero no eliminarlo para conservar el historial.");
                    }
                    Execute(connection, transaction, "DELETE FROM producto_variante WHERE id_producto = @id", new Dictionary<string, object> { { "@id", productId } });
                    Execute(connection, transaction, "DELETE FROM producto WHERE id_producto = @id", new Dictionary<string, object> { { "@id", productId } });
                    RegistrarAuditoria(connection, transaction, AuditActions.ProductoEliminado,
                        "Artículo " + product.Name + " (" + product.Category + ")" +
                        (product.Options != null && product.Options.Count > 0 ? ", con " + product.Options.Count + " variantes" : ""));
                    if (advanceFingerprint) newFingerprint = ComputeCatalogFingerprint(connection, transaction, false);
                    transaction.Commit();
                }
                if (advanceFingerprint) catalogFingerprint = newFingerprint;
                RaiseChanged();
            }
        }

        // expectedDiscount >= 0: descuento y promoción (expectedPromotionId, null = ninguna) que el diálogo de cobro mostró al cajero. AddReceipt
        // siempre recalcula la promoción dentro de su transacción; si difiere de lo mostrado, cancela para volver a cobrar.
        public static Receipt AddReceipt(List<ReceiptLine> lines, string customer, string customerDni, string method, decimal received, decimal cashAmount = -1m, decimal yapeAmount = -1m,
            decimal expectedDiscount = -1m, string expectedPromotionId = null, string savedTicketId = null)
        {
            if (lines == null || lines.Count == 0) throw new ArgumentException("El comprobante debe tener al menos una línea.");
            foreach (var line in lines)
                if (line.Quantity <= 0) throw new ArgumentException("La cantidad de \"" + line.Name + "\" debe ser mayor que cero.");
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Seleccione un medio de pago.");
            lock (Sync)
            {
                Receipt receipt;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    // FOR UPDATE: el número de recibo y su incremento van en la misma transacción que el INSERT (sin repetidos).
                    int number;
                    string defaultEmployee;
                    using (var command = new MySqlCommand("SELECT next_receipt_number, employee_name FROM configuracion WHERE id = 1 FOR UPDATE", connection, transaction))
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("La configuración del negocio no está inicializada.");
                        number = reader.GetInt32(0);
                        defaultEmployee = reader.GetString(1);
                    }
                    // Ticket guardado que se está cobrando: se bloquea aquí (si otra caja ya lo cobró o lo eliminó, se aborta sin vender) y se borra
                    // con sus líneas y llaves antes del Commit, de modo que venta y liberación de llaves son atómicas.
                    var hasSavedTicket = !string.IsNullOrWhiteSpace(savedTicketId);
                    if (hasSavedTicket)
                    {
                        if (!LockSavedTicket(connection, transaction, savedTicketId))
                            throw new SavedTicketUnavailableException();
                    }
                    // Los artículos se leen con FOR UPDATE: el saldo validado es el que se descuenta.
                    var products = LoadProductsFromDb(connection, transaction, true);
                    var changes = new List<StockChange>();
                    foreach (var line in lines)
                    {
                        var product = products.FirstOrDefault(x => x.Id == line.ProductId);
                        if (product == null) continue;
                        var option = FindLineOption(product, line);
                        if (option != null && option.TrackInventory && option.Stock < line.Quantity)
                            throw new InvalidOperationException("No hay saldo suficiente de " + option.Name + ". Disponible: " + option.Stock + ".");
                        if (option == null && product.TrackInventory && product.Stock < line.Quantity)
                            throw new InvalidOperationException("No hay saldo suficiente de " + product.Name + ". Disponible: " + product.Stock + ".");
                    }
                    var openShift = ReadOpenShift(connection, transaction);
                    // Promoción recalculada con el historial y la configuración vigentes dentro de esta transacción (RN-03/04/05).
                    var promotion = EvaluatePromotion(connection, transaction, customerDni, lines);
                    if (expectedDiscount >= 0m &&
                        (promotion.Descuento != Math.Round(expectedDiscount, 2) || (promotion.PromocionId ?? "") != (expectedPromotionId ?? "")))
                        throw new InvalidOperationException("La promoción aplicable cambió mientras se cobraba (" +
                            (promotion.Aplica ? promotion.Descripcion + ", descuento " + Theme.Money(promotion.Descuento) : "ahora no corresponde ninguna promoción") +
                            "). No se registró el cobro: vuelva a cobrar el ticket para ver el total actualizado.");
                    receipt = new Receipt
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Number = number,
                        CreatedAt = NowWithoutFraction(),
                        Employee = Clip(string.IsNullOrWhiteSpace(AppSession.UserName) ? defaultEmployee : AppSession.UserName, 60),
                        CustomerName = Clip(string.IsNullOrWhiteSpace(customer) ? "Público general" : customer.Trim(), 150),
                        CustomerDni = Validation.NormalizeDni(customerDni),
                        PaymentMethod = Clip(method, 10),
                        AmountReceived = Math.Round(received, 2),
                        Discount = promotion.Descuento,
                        PromotionId = promotion.Aplica ? promotion.PromocionId : null,
                        // Las líneas conservan su precio original; el descuento va a nivel recibo y Total es lo que se cobra.
                        Total = Math.Round(lines.Sum(x => x.LineTotal), 2) - promotion.Descuento,
                        Status = "Pagado",
                        Lines = lines.Select(x => new ReceiptLine
                        {
                            ProductId = x.ProductId,
                            OptionId = x.OptionId,
                            Name = Clip(x.Name, 120),
                            Comment = x.Comment,
                            UnitPrice = x.UnitPrice,
                            Quantity = x.Quantity
                        }).ToList()
                    };
                    // RN-11: por encima de S/ 700 (total ya con descuento) el DNI es obligatorio. Se lanza antes de escribir nada.
                    if (receipt.Total > DniObligatorioDesde && !Validation.IsValidDni(receipt.CustomerDni))
                        throw new ArgumentException("Las ventas mayores a " + Theme.Money(DniObligatorioDesde) + " requieren un DNI válido de 8 dígitos del cliente. Ingrese el DNI para cobrar.");
                    receipt.CashAmount = Math.Round(cashAmount >= 0m ? cashAmount : method == "Efectivo" ? receipt.Total : 0m, 2);
                    receipt.YapeAmount = Math.Round(yapeAmount >= 0m ? yapeAmount : method == "Yape" ? receipt.Total : 0m, 2);
                    // Pago validado en el servidor (no solo en el diálogo de cobro).
                    if (method == "Efectivo" && receipt.AmountReceived < receipt.Total)
                        throw new ArgumentException("El efectivo recibido (" + Theme.Money(receipt.AmountReceived) + ") es menor que el total (" + Theme.Money(receipt.Total) + ").");
                    if (method == "Mixto")
                    {
                        if (cashAmount < 0m || yapeAmount < 0m)
                            throw new ArgumentException("En pago mixto indique los montos de efectivo y Yape (no pueden ser negativos).");
                        if (receipt.CashAmount + receipt.YapeAmount != Math.Round(receipt.Total, 2))
                            throw new ArgumentException("En pago mixto, efectivo (" + Theme.Money(receipt.CashAmount) + ") más Yape (" + Theme.Money(receipt.YapeAmount) +
                                ") debe sumar exactamente el total (" + Theme.Money(receipt.Total) + ").");
                    }
                    foreach (var line in receipt.Lines)
                    {
                        var product = products.FirstOrDefault(x => x.Id == line.ProductId);
                        if (product == null) continue;
                        var option = FindLineOption(product, line);
                        if (option != null)
                        {
                            if (!option.TrackInventory) continue;
                            var previous = option.Stock;
                            option.Stock -= line.Quantity;
                            changes.Add(new StockChange(product, option, NewInventoryMovement(product, option, "Venta", line.Quantity, previous, "Venta #" + receipt.Number, receipt.Id)));
                        }
                        else
                        {
                            if (!product.TrackInventory) continue;
                            var previous = product.Stock;
                            product.Stock -= line.Quantity;
                            changes.Add(new StockChange(product, null, NewInventoryMovement(product, "Venta", line.Quantity, previous, "Venta #" + receipt.Number, receipt.Id)));
                        }
                    }
                    // id_sesion: turno abierto al vender (NULL si no hay).
                    Execute(connection, transaction,
                        "INSERT INTO recibo (id_recibo, numero, id_sesion, created_at, empleado, cliente_nombre, cliente_dni, medio_pago, monto_recibido, monto_efectivo, monto_yape, descuento_promocion, id_promocion_aplicada, total, estado) " +
                        "VALUES (@id, @numero, @sesion, @fecha, @empleado, @cliente, @dni, @medio, @recibido, @efectivo, @yape, @descuento, @promocion, @total, @estado)",
                        new Dictionary<string, object>
                        {
                            { "@id", receipt.Id }, { "@numero", receipt.Number },
                            { "@sesion", openShift == null ? null : openShift.Id },
                            { "@fecha", receipt.CreatedAt }, { "@empleado", receipt.Employee },
                            { "@cliente", receipt.CustomerName },
                            { "@dni", string.IsNullOrEmpty(receipt.CustomerDni) ? null : receipt.CustomerDni },
                            { "@medio", receipt.PaymentMethod }, { "@recibido", receipt.AmountReceived },
                            { "@efectivo", receipt.CashAmount }, { "@yape", receipt.YapeAmount },
                            { "@descuento", receipt.Discount }, { "@promocion", receipt.PromotionId },
                            { "@total", receipt.Total }, { "@estado", receipt.Status }
                        });
                    var order = 0;
                    foreach (var line in receipt.Lines)
                        Execute(connection, transaction,
                            "INSERT INTO recibo_linea (id_recibo, orden, id_producto, id_variante, nombre, comentario, precio_unitario, cantidad) " +
                            "VALUES (@recibo, @orden, @producto, @variante, @nombre, @comentario, @precio, @cantidad)",
                            new Dictionary<string, object>
                            {
                                { "@recibo", receipt.Id }, { "@orden", order++ },
                                { "@producto", string.IsNullOrWhiteSpace(line.ProductId) ? null : line.ProductId },
                                { "@variante", string.IsNullOrWhiteSpace(line.OptionId) ? null : line.OptionId },
                                { "@nombre", line.Name }, { "@comentario", string.IsNullOrEmpty(line.Comment) ? null : Clip(line.Comment, 150) },
                                { "@precio", Math.Round(line.UnitPrice, 2) }, { "@cantidad", line.Quantity }
                            });
                    // Comprobante electrónico simulado en estado Pendiente, dentro de la misma transacción del cobro.
                    receipt.Comprobante = CrearComprobante(connection, transaction, receipt);
                    PersistStockChanges(connection, transaction, changes);
                    PersistNextReceiptNumber(connection, transaction, number + 1);
                    if (hasSavedTicket)
                    {
                        DeleteTicketChildren(connection, transaction, savedTicketId);
                        Execute(connection, transaction, "DELETE FROM ticket_guardado WHERE id_ticket = @id",
                            new Dictionary<string, object> { { "@id", savedTicketId } });
                    }
                    transaction.Commit();
                }
                // El envío al emisor (simulado) va en segundo plano, después del commit: no bloquea la UI ni la venta.
                ElectronicInvoicingService.EnviarEnSegundoPlano(receipt.Comprobante.Id);
                RaiseChanged();
                return receipt;
            }
        }

        public static SavedTicket SaveTicket(List<ReceiptLine> lines, string customer, string customerDni, string name, int lockerNumber = 0)
        {
            return SaveTicket(lines, customer, customerDni, name, lockerNumber > 0 ? new[] { lockerNumber } : new int[0], 0, 0);
        }

        public static SavedTicket SaveTicket(
            List<ReceiptLine> lines,
            string customer,
            string customerDni,
            string name,
            IEnumerable<int> lockerNumbers,
            int adultCount = 0,
            int childCount = 0)
        {
            if (lines == null || lines.Count == 0) throw new InvalidOperationException("No hay artículos para guardar.");
            var selectedLockers = (lockerNumbers ?? new int[0]).Distinct().OrderBy(x => x).ToList();
            if (selectedLockers.Count == 0) throw new ArgumentException("Seleccione al menos una llave.");
            if (selectedLockers.Any(x => x < 1 || x > 35)) throw new ArgumentException("Los candados deben estar entre 1 y 35.");
            if (adultCount < 0 || adultCount > 999 || childCount < 0 || childCount > 999)
                throw new ArgumentException("La cantidad de personas no es válida.");
            lock (Sync)
            {
                var now = DateTime.Now;
                var ticket = new SavedTicket
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(name) ? "Ticket " + now.ToString("HH:mm") : name.Trim(),
                    CustomerName = string.IsNullOrWhiteSpace(customer) ? "Público general" : customer.Trim(),
                    CustomerDni = Validation.NormalizeDni(customerDni),
                    LockerNumber = selectedLockers.First(),
                    LockerNumbers = selectedLockers,
                    AdultCount = adultCount,
                    ChildCount = childCount,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Lines = CopyLines(lines)
                };
                try
                {
                    using (var connection = SqlConnectionFactory.Open())
                    using (var transaction = connection.BeginTransaction())
                    {
                        var duplicated = FindOccupiedLocker(connection, transaction, null, selectedLockers);
                        if (duplicated > 0) throw new InvalidOperationException("El candado " + duplicated + " ya está ocupado. Seleccione otro.");
                        Execute(connection, transaction,
                            "INSERT INTO ticket_guardado (id_ticket, nombre, cliente_nombre, cliente_dni, adultos, ninos, created_at, updated_at) " +
                            "VALUES (@id, @nombre, @cliente, @dni, @adultos, @ninos, @creado, @actualizado)",
                            TicketValues(ticket));
                        WriteTicketChildren(connection, transaction, ticket);
                        transaction.Commit();
                    }
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    throw new InvalidOperationException("Una de las llaves ya está ocupada. Seleccione otra.", ex);
                }
                RaiseChanged();
                return ticket;
            }
        }

        public static SavedTicket UpdateSavedTicket(
            string id,
            List<ReceiptLine> lines,
            string customer,
            string customerDni,
            string name,
            IEnumerable<int> lockerNumbers,
            int adultCount,
            int childCount)
        {
            if (lines == null || lines.Count == 0) throw new InvalidOperationException("No hay artículos para guardar.");
            var selectedLockers = (lockerNumbers ?? new int[0]).Distinct().OrderBy(x => x).ToList();
            if (selectedLockers.Count == 0) throw new ArgumentException("Seleccione al menos una llave.");
            if (selectedLockers.Any(x => x < 1 || x > 35)) throw new ArgumentException("Las llaves deben estar entre 1 y 35.");
            if (adultCount < 0 || adultCount > 999 || childCount < 0 || childCount > 999)
                throw new ArgumentException("La cantidad de personas no es válida.");
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Seleccione un ticket guardado.");
            lock (Sync)
            {
                SavedTicket ticket;
                try
                {
                    using (var connection = SqlConnectionFactory.Open())
                    using (var transaction = connection.BeginTransaction())
                    {
                        LockSavedTicket(connection, transaction, id);
                        ticket = LoadSavedTicketsFromDb(connection, transaction, id).FirstOrDefault();
                        if (ticket == null) throw new InvalidOperationException("El ticket guardado ya no existe.");
                        var duplicated = FindOccupiedLocker(connection, transaction, id, selectedLockers);
                        if (duplicated > 0) throw new InvalidOperationException("La llave " + duplicated + " ya está ocupada. Seleccione otra.");

                        ticket.Name = string.IsNullOrWhiteSpace(name) ? ticket.Name : name.Trim();
                        ticket.CustomerName = string.IsNullOrWhiteSpace(customer) ? "Público general" : customer.Trim();
                        ticket.CustomerDni = Validation.NormalizeDni(customerDni);
                        ticket.LockerNumber = selectedLockers.First();
                        ticket.LockerNumbers = selectedLockers;
                        ticket.AdultCount = adultCount;
                        ticket.ChildCount = childCount;
                        ticket.UpdatedAt = DateTime.Now;
                        ticket.Lines = CopyLines(lines);
                        Execute(connection, transaction,
                            "UPDATE ticket_guardado SET nombre = @nombre, cliente_nombre = @cliente, cliente_dni = @dni, adultos = @adultos, " +
                            "ninos = @ninos, updated_at = @actualizado WHERE id_ticket = @id",
                            TicketValues(ticket));
                        DeleteTicketChildren(connection, transaction, id);
                        WriteTicketChildren(connection, transaction, ticket);
                        transaction.Commit();
                    }
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    throw new InvalidOperationException("Una de las llaves ya está ocupada. Seleccione otra.", ex);
                }
                RaiseChanged();
                return ticket;
            }
        }

        public static List<int> GetTicketLockers(SavedTicket ticket)
        {
            if (ticket == null) return new List<int>();
            if (ticket.LockerNumbers != null && ticket.LockerNumbers.Count > 0) return ticket.LockerNumbers.Distinct().OrderBy(x => x).ToList();
            return ticket.LockerNumber > 0 ? new List<int> { ticket.LockerNumber } : new List<int>();
        }

        public static int GetTicketAdultCount(SavedTicket ticket)
        {
            if (ticket == null) return 0;
            if (ticket.AdultCount > 0 || ticket.ChildCount > 0) return Math.Max(0, ticket.AdultCount);
            int adults;
            int children;
            OccupancyCalculator.Count(ticket.Lines, out adults, out children);
            return adults;
        }

        public static int GetTicketChildCount(SavedTicket ticket)
        {
            if (ticket == null) return 0;
            if (ticket.AdultCount > 0 || ticket.ChildCount > 0) return Math.Max(0, ticket.ChildCount);
            int adults;
            int children;
            OccupancyCalculator.Count(ticket.Lines, out adults, out children);
            return children;
        }

        public static void UpdateSavedTicketLockers(string id, IEnumerable<int> lockerNumbers)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Seleccione un ticket guardado.");
            lock (Sync)
            {
                var selectedLockers = (lockerNumbers ?? new int[0]).Distinct().OrderBy(x => x).ToList();
                if (selectedLockers.Count == 0) throw new ArgumentException("Seleccione al menos una llave.");
                if (selectedLockers.Any(x => x < 1 || x > 35)) throw new ArgumentException("Las llaves deben estar entre 1 y 35.");

                try
                {
                    using (var connection = SqlConnectionFactory.Open())
                    using (var transaction = connection.BeginTransaction())
                    {
                        // Orden de bloqueo padre -> hijos (igual que AddReceipt): primero la fila de ticket_guardado.
                        LockSavedTicket(connection, transaction, id);
                        var ticket = LoadSavedTicketsFromDb(connection, transaction, id).FirstOrDefault();
                        if (ticket == null) throw new InvalidOperationException("El ticket guardado ya no existe.");

                        var duplicated = FindOccupiedLocker(connection, transaction, id, selectedLockers);
                        if (duplicated > 0) throw new InvalidOperationException("La llave " + duplicated + " ya está ocupada. Seleccione otra.");

                        Execute(connection, transaction, "DELETE FROM casillero_llave WHERE id_ticket = @id",
                            new Dictionary<string, object> { { "@id", id } });
                        foreach (var locker in selectedLockers)
                            Execute(connection, transaction, "INSERT INTO casillero_llave (id_ticket, numero) VALUES (@id, @numero)",
                                new Dictionary<string, object> { { "@id", id }, { "@numero", locker } });
                        Execute(connection, transaction, "UPDATE ticket_guardado SET updated_at = @actualizado WHERE id_ticket = @id",
                            new Dictionary<string, object> { { "@id", id }, { "@actualizado", DateTime.Now } });
                        transaction.Commit();
                    }
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    throw new InvalidOperationException("Una de las llaves ya está ocupada. Seleccione otra.", ex);
                }
                RaiseChanged();
            }
        }

        public static void DeleteSavedTicket(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Seleccione un ticket guardado.");
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    // Orden de bloqueo padre -> hijos (igual que AddReceipt): primero la fila de ticket_guardado.
                    LockSavedTicket(connection, transaction, id);
                    DeleteTicketChildren(connection, transaction, id);
                    Execute(connection, transaction, "DELETE FROM ticket_guardado WHERE id_ticket = @id",
                        new Dictionary<string, object> { { "@id", id ?? "" } });
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        // SELECT ... FOR UPDATE sobre la fila padre; devuelve false si ya no existe.
        private static bool LockSavedTicket(MySqlConnection connection, MySqlTransaction transaction, string id)
        {
            using (var command = new MySqlCommand("SELECT 1 FROM ticket_guardado WHERE id_ticket = @id FOR UPDATE", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", id ?? "");
                return command.ExecuteScalar() != null;
            }
        }

        // Devuelve la primera llave de la lista que ya usa otro ticket (excluyendo exceptId), o 0 si todas están libres.
        private static int FindOccupiedLocker(MySqlConnection connection, MySqlTransaction transaction, string exceptId, List<int> lockers)
        {
            var occupied = new HashSet<int>();
            using (var command = new MySqlCommand("SELECT numero FROM casillero_llave WHERE id_ticket <> @id", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", exceptId ?? "");
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) occupied.Add(reader.GetInt32(0));
                }
            }
            return lockers.FirstOrDefault(x => occupied.Contains(x));
        }

        private static Dictionary<string, object> TicketValues(SavedTicket ticket)
        {
            return new Dictionary<string, object>
            {
                { "@id", ticket.Id },
                { "@nombre", Clip(ticket.Name, 80) },
                { "@cliente", Clip(ticket.CustomerName, 150) },
                { "@dni", Clip(ticket.CustomerDni, 8) },
                { "@adultos", ticket.AdultCount },
                { "@ninos", ticket.ChildCount },
                { "@creado", ticket.CreatedAt },
                { "@actualizado", ticket.UpdatedAt }
            };
        }

        private static void DeleteTicketChildren(MySqlConnection connection, MySqlTransaction transaction, string id)
        {
            var values = new Dictionary<string, object> { { "@id", id ?? "" } };
            Execute(connection, transaction, "DELETE FROM ticket_linea WHERE id_ticket = @id", values);
            Execute(connection, transaction, "DELETE FROM casillero_llave WHERE id_ticket = @id", values);
        }

        // Líneas (con su orden de captura) y llaves del ticket.
        private static void WriteTicketChildren(MySqlConnection connection, MySqlTransaction transaction, SavedTicket ticket)
        {
            var order = 0;
            foreach (var line in ticket.Lines ?? new List<ReceiptLine>())
            {
                Execute(connection, transaction,
                    "INSERT INTO ticket_linea (id_ticket, orden, id_producto, id_variante, nombre, comentario, precio_unitario, cantidad) " +
                    "VALUES (@id, @orden, @producto, @variante, @nombre, @comentario, @precio, @cantidad)",
                    new Dictionary<string, object>
                    {
                        { "@id", ticket.Id },
                        { "@orden", order++ },
                        { "@producto", string.IsNullOrEmpty(line.ProductId) ? null : Clip(line.ProductId, 32) },
                        { "@variante", string.IsNullOrEmpty(line.OptionId) ? null : Clip(line.OptionId, 32) },
                        { "@nombre", Clip(line.Name, 120) },
                        { "@comentario", string.IsNullOrEmpty(line.Comment) ? null : Clip(line.Comment, 150) },
                        { "@precio", Math.Round(line.UnitPrice, 2) },
                        { "@cantidad", line.Quantity }
                    });
            }
            foreach (var locker in GetTicketLockers(ticket))
                Execute(connection, transaction, "INSERT INTO casillero_llave (id_ticket, numero) VALUES (@id, @numero)",
                    new Dictionary<string, object> { { "@id", ticket.Id }, { "@numero", locker } });
        }

        // Carga todos los tickets (onlyId == null) o uno solo, con líneas y llaves en su orden.
        private static List<SavedTicket> LoadSavedTicketsFromDb(MySqlConnection connection, MySqlTransaction transaction, string onlyId)
        {
            var tickets = new List<SavedTicket>();
            var byId = new Dictionary<string, SavedTicket>();
            var filter = onlyId == null ? "" : " WHERE id_ticket = @id";
            using (var command = new MySqlCommand(
                "SELECT id_ticket, nombre, cliente_nombre, cliente_dni, adultos, ninos, created_at, updated_at FROM ticket_guardado" +
                filter + " ORDER BY created_at, id_ticket", connection, transaction))
            {
                if (onlyId != null) command.Parameters.AddWithValue("@id", onlyId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var ticket = new SavedTicket
                        {
                            Id = reader.GetString(0),
                            Name = reader.GetString(1),
                            CustomerName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            CustomerDni = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            AdultCount = reader.GetInt32(4),
                            ChildCount = reader.GetInt32(5),
                            CreatedAt = reader.GetDateTime(6),
                            UpdatedAt = reader.GetDateTime(7)
                        };
                        tickets.Add(ticket);
                        byId[ticket.Id] = ticket;
                    }
                }
            }
            if (tickets.Count == 0) return tickets;

            using (var command = new MySqlCommand(
                "SELECT id_ticket, id_producto, id_variante, nombre, comentario, precio_unitario, cantidad FROM ticket_linea" +
                filter + " ORDER BY id_ticket, orden", connection, transaction))
            {
                if (onlyId != null) command.Parameters.AddWithValue("@id", onlyId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        SavedTicket ticket;
                        if (!byId.TryGetValue(reader.GetString(0), out ticket)) continue;
                        ticket.Lines.Add(new ReceiptLine
                        {
                            ProductId = reader.IsDBNull(1) ? null : reader.GetString(1),
                            OptionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                            Name = reader.GetString(3),
                            Comment = reader.IsDBNull(4) ? null : reader.GetString(4),
                            UnitPrice = reader.GetDecimal(5),
                            Quantity = reader.GetInt32(6)
                        });
                    }
                }
            }

            using (var command = new MySqlCommand(
                "SELECT id_ticket, numero FROM casillero_llave" + filter + " ORDER BY id_ticket, numero", connection, transaction))
            {
                if (onlyId != null) command.Parameters.AddWithValue("@id", onlyId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        SavedTicket ticket;
                        if (!byId.TryGetValue(reader.GetString(0), out ticket)) continue;
                        ticket.LockerNumbers.Add(reader.GetInt32(1));
                    }
                }
            }
            foreach (var ticket in tickets)
                ticket.LockerNumber = ticket.LockerNumbers.Count > 0 ? ticket.LockerNumbers[0] : 0;
            return tickets;
        }

        private static List<ReceiptLine> CopyLines(IEnumerable<ReceiptLine> lines)
        {
            return (lines ?? Enumerable.Empty<ReceiptLine>()).Select(x => new ReceiptLine
            {
                ProductId = x.ProductId,
                OptionId = x.OptionId,
                Name = x.Name,
                Comment = x.Comment,
                UnitPrice = x.UnitPrice,
                Quantity = x.Quantity
            }).ToList();
        }

        public static Customer FindCustomer(string dni)
        {
            var normalized = Validation.NormalizeDni(dni);
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                    return ReadCustomer(connection, normalized);
            }
        }

        public static Customer SaveCustomer(string dni, string fullName, string source)
        {
            var normalized = Validation.NormalizeDni(dni);
            if (!Validation.IsValidDni(normalized)) throw new ArgumentException("El DNI debe tener 8 dígitos.");
            if (!Validation.IsValidPersonName(fullName)) throw new ArgumentException("El nombre completo no es válido.");
            lock (Sync)
            {
                var now = DateTime.Now;
                Customer customer;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    // Si el DNI ya existe se actualizan nombre y origen y se conserva la fecha de alta.
                    Execute(connection, transaction,
                        "INSERT INTO cliente (dni, nombre_completo, origen, created_at, updated_at) VALUES (@dni, @nombre, @origen, @fecha, @fecha) " +
                        "ON DUPLICATE KEY UPDATE nombre_completo = VALUES(nombre_completo), origen = VALUES(origen), updated_at = VALUES(updated_at)",
                        new Dictionary<string, object>
                        {
                            { "@dni", normalized },
                            { "@nombre", Clip(Validation.NormalizeName(fullName), 150) },
                            { "@origen", Clip(string.IsNullOrWhiteSpace(source) ? "Manual" : source, 60) },
                            { "@fecha", now }
                        });
                    transaction.Commit();
                }
                using (var connection = SqlConnectionFactory.Open())
                    customer = ReadCustomer(connection, normalized);
                RaiseChanged();
                return customer;
            }
        }

        public static void ClearCustomers()
        {
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction, "DELETE FROM cliente", new Dictionary<string, object>());
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        private static Customer ReadCustomer(MySqlConnection connection, string dni)
        {
            using (var command = new MySqlCommand(
                "SELECT dni, nombre_completo, origen, created_at, updated_at FROM cliente WHERE dni = @dni", connection))
            {
                command.Parameters.AddWithValue("@dni", dni ?? "");
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? CustomerFromReader(reader) : null;
            }
        }

        private static List<Customer> LoadCustomersFromDb()
        {
            var customers = new List<Customer>();
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(
                "SELECT dni, nombre_completo, origen, created_at, updated_at FROM cliente ORDER BY created_at, dni", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) customers.Add(CustomerFromReader(reader));
            }
            return customers;
        }

        private static Customer CustomerFromReader(MySqlDataReader reader)
        {
            return new Customer
            {
                Dni = reader.GetString(0),
                FullName = reader.GetString(1),
                Source = reader.GetString(2),
                CreatedAt = reader.GetDateTime(3),
                UpdatedAt = reader.GetDateTime(4)
            };
        }

        public static string ProtectSecret(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var bytes = Encoding.UTF8.GetBytes(value.Trim());
            var protectedBytes = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("EdenPOS-DNI-v1"), DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static string UnprotectSecret(string protectedValue)
        {
            var environmentToken = Environment.GetEnvironmentVariable("EDENPOS_DNI_TOKEN");
            if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken.Trim();
            if (string.IsNullOrWhiteSpace(protectedValue)) return "";
            try
            {
                var bytes = Convert.FromBase64String(protectedValue);
                var clearBytes = ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes("EdenPOS-DNI-v1"), DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch { return ""; }
        }

        public static void RefundReceipt(Receipt receipt, string reason)
        {
            if (receipt == null) throw new ArgumentNullException("receipt");
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    // El cambio de estado es condicional: si ya estaba reembolsado (o no existe), no se toca nada.
                    int updated;
                    using (var command = new MySqlCommand(
                        "UPDATE recibo SET estado = 'Reembolsado', refunded_at = @fecha, refund_reason = @motivo WHERE id_recibo = @id AND estado = 'Pagado'", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", receipt.Id ?? "");
                        command.Parameters.AddWithValue("@fecha", NowWithoutFraction());
                        command.Parameters.AddWithValue("@motivo", string.IsNullOrEmpty(reason) ? (object)DBNull.Value : Clip(reason, 200));
                        updated = command.ExecuteNonQuery();
                    }
                    if (updated == 0) return;
                    var stored = ReadReceipts(connection, transaction, receipt.Id).FirstOrDefault();
                    if (stored == null) return;
                    var products = LoadProductsFromDb(connection, transaction, true);
                    var changes = new List<StockChange>();
                    foreach (var line in stored.Lines ?? new List<ReceiptLine>())
                    {
                        var product = products.FirstOrDefault(x => x.Id == line.ProductId);
                        if (product == null) continue;
                        var option = FindLineOption(product, line);
                        if (option != null)
                        {
                            if (!option.TrackInventory) continue;
                            var previous = option.Stock;
                            option.Stock += line.Quantity;
                            changes.Add(new StockChange(product, option, NewInventoryMovement(product, option, "Reembolso", line.Quantity, previous, "Reembolso #" + stored.Number, stored.Id)));
                        }
                        else
                        {
                            if (!product.TrackInventory) continue;
                            var previous = product.Stock;
                            product.Stock += line.Quantity;
                            changes.Add(new StockChange(product, null, NewInventoryMovement(product, "Reembolso", line.Quantity, previous, "Reembolso #" + stored.Number, stored.Id)));
                        }
                    }
                    PersistStockChanges(connection, transaction, changes);
                    var anulado = AnularComprobante(connection, transaction, stored.Id);
                    RegistrarAuditoria(connection, transaction, AuditActions.ReciboReembolsado,
                        "Recibo #" + stored.Number + ", total " + stored.Total.ToString("0.00", CultureInfo.InvariantCulture) +
                        ", motivo: " + (string.IsNullOrWhiteSpace(reason) ? "(sin motivo)" : reason.Trim()) +
                        (anulado == null ? "" : ". Comprobante " + anulado + " anulado (simulado, sin nota de crédito)"));
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        // Inserta una fila de auditoría con la MISMA conexión y transacción de la operación auditada: si esa operación
        // hace rollback, la auditoría desaparece con ella. id_usuario solo se guarda si el usuario existe en este momento
        // (tras restaurar una copia o importar datos antiguos la tabla usuario puede haber cambiado).
        // delSistema = true: acción automática (hilo de fondo): id_usuario NULL y el detalle empieza con "Sistema: ".
        private static void RegistrarAuditoria(MySqlConnection connection, MySqlTransaction transaction, string accion, string detalle, bool delSistema = false)
        {
            object userId = DBNull.Value;
            int parsed;
            var current = delSistema ? null : AppSession.CurrentUser;
            if (delSistema) detalle = "Sistema: " + detalle;
            if (current != null && int.TryParse(current.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) userId = parsed;
            using (var command = new MySqlCommand(
                "INSERT INTO auditoria (id_usuario, accion, detalle, created_at) " +
                "VALUES ((SELECT id_usuario FROM usuario WHERE id_usuario = @usuario), @accion, @detalle, @fecha)", connection, transaction))
            {
                command.Parameters.AddWithValue("@usuario", userId);
                command.Parameters.AddWithValue("@accion", Clip(accion, 60));
                command.Parameters.AddWithValue("@detalle", Clip(detalle, 300));
                command.Parameters.AddWithValue("@fecha", NowWithoutFraction());
                command.ExecuteNonQuery();
            }
        }

        // Consulta para la futura pantalla de auditoría (Task 3.4). usuarioId null/vacío = todos; hasta incluye todo el día indicado.
        public static List<AuditEntry> GetAuditoria(DateTime desde, DateTime hasta, string usuarioId)
        {
            var entries = new List<AuditEntry>();
            var from = desde.Date;
            var to = hasta.Date.AddDays(1);
            int userFilter = 0;
            var filterByUser = !string.IsNullOrWhiteSpace(usuarioId);
            if (filterByUser && !int.TryParse(usuarioId, NumberStyles.Integer, CultureInfo.InvariantCulture, out userFilter))
                return entries;
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(
                "SELECT a.created_at, u.nombre, a.accion, a.detalle FROM auditoria a LEFT JOIN usuario u ON u.id_usuario = a.id_usuario " +
                "WHERE a.created_at >= @desde AND a.created_at < @hasta" + (filterByUser ? " AND a.id_usuario = @usuario" : "") +
                " ORDER BY a.created_at DESC, a.id_auditoria DESC LIMIT 5000", connection))
            {
                command.Parameters.AddWithValue("@desde", from);
                command.Parameters.AddWithValue("@hasta", to);
                if (filterByUser) command.Parameters.AddWithValue("@usuario", userFilter);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        entries.Add(new AuditEntry
                        {
                            Date = reader.GetDateTime(0),
                            User = reader.IsDBNull(1) ? "—" : reader.GetString(1),
                            Action = reader.GetString(2),
                            Detail = reader.GetString(3)
                        });
            }
            return entries;
        }

        public const int PromoMinPersonas = 2;
        public const int PromoMaxPersonas = 50;
        public const decimal PromoMaxPorcentaje = 50m;

        // Vista previa de la promoción (diálogo de cobro): conexión propia, sin transacción.
        public static PromocionResultado CalculatePromotion(string customerDni, List<ReceiptLine> lines)
        {
            if (lines == null || lines.Count == 0) return PromocionResultado.Ninguna;
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
                return EvaluatePromotion(connection, null, customerDni, lines);
        }

        // Arma el contexto con SQL agregado (sin cargar recibos): categoría SAUNA de los productos de las líneas y conteo de visitas
        // del DNI en una sola consulta. Se usa con la transacción de AddReceipt o sin ella (transaction null).
        private static PromocionResultado EvaluatePromotion(MySqlConnection connection, MySqlTransaction transaction, string customerDni, List<ReceiptLine> lines)
        {
            var settings = ReadConfiguration(connection, transaction);
            var dni = Validation.NormalizeDni(customerDni);
            var context = new PromocionContexto
            {
                ClienteDni = dni,
                Lineas = lines,
                SextaVisitaActiva = settings.PromoSextaVisitaActiva,
                GrupalMinimoPersonas = settings.PromoGrupalMinimoPersonas,
                GrupalPorcentaje = settings.PromoGrupalPorcentaje
            };
            var productIds = lines.Where(x => !string.IsNullOrEmpty(x.ProductId)).Select(x => x.ProductId).Distinct().ToList();
            if (productIds.Count > 0)
            {
                var names = productIds.Select((x, index) => "@p" + index).ToList();
                using (var command = new MySqlCommand(
                    "SELECT id_producto FROM producto WHERE categoria = 'SAUNA' AND id_producto IN (" + string.Join(", ", names) + ")", connection, transaction))
                {
                    for (var index = 0; index < productIds.Count; index++) command.Parameters.AddWithValue(names[index], productIds[index]);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) context.ProductosSauna.Add(reader.GetString(0));
                }
            }
            if (settings.PromoSextaVisitaActiva && Validation.IsValidDni(dni) && context.ProductosSauna.Count > 0)
            {
                using (var command = new MySqlCommand(
                    "SELECT COALESCE(SUM(CASE WHEN r.id_promocion_aplicada = 'SEXTA_VISITA' THEN 0 ELSE 1 END), 0), " +
                    "COALESCE(SUM(CASE WHEN r.id_promocion_aplicada = 'SEXTA_VISITA' THEN 1 ELSE 0 END), 0) " +
                    "FROM recibo r WHERE r.estado = 'Pagado' AND r.cliente_dni = @dni AND EXISTS (" +
                    "SELECT 1 FROM recibo_linea l INNER JOIN producto p ON p.id_producto = l.id_producto " +
                    "WHERE l.id_recibo = r.id_recibo AND p.categoria = 'SAUNA')", connection, transaction))
                {
                    command.Parameters.AddWithValue("@dni", dni);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            context.VisitasPagadas = Convert.ToInt32(reader.GetValue(0));
                            context.VisitasGratis = Convert.ToInt32(reader.GetValue(1));
                        }
                    }
                }
            }
            return PromocionCalculadora.Evaluar(context);
        }

        // Lee recibos con sus líneas en dos consultas (sin N+1). receiptId null = todos, ordenados por fecha y número.
        private static List<Receipt> ReadReceipts(MySqlConnection connection, MySqlTransaction transaction, string receiptId)
        {
            var receipts = new List<Receipt>();
            var byId = new Dictionary<string, Receipt>();
            var filter = receiptId == null ? "" : "WHERE id_recibo = @id ";
            using (var command = new MySqlCommand(
                "SELECT id_recibo, numero, created_at, empleado, cliente_nombre, cliente_dni, medio_pago, monto_recibido, monto_efectivo, monto_yape, total, estado, refunded_at, refund_reason, descuento_promocion, id_promocion_aplicada " +
                "FROM recibo " + filter + "ORDER BY created_at, numero", connection, transaction))
            {
                if (receiptId != null) command.Parameters.AddWithValue("@id", receiptId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var receipt = new Receipt
                        {
                            Id = reader.GetString(0),
                            Number = reader.GetInt32(1),
                            CreatedAt = reader.GetDateTime(2),
                            Employee = reader.GetString(3),
                            CustomerName = reader.GetString(4),
                            CustomerDni = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            PaymentMethod = reader.GetString(6),
                            AmountReceived = reader.GetDecimal(7),
                            CashAmount = reader.GetDecimal(8),
                            YapeAmount = reader.GetDecimal(9),
                            Total = reader.GetDecimal(10),
                            Status = reader.GetString(11),
                            RefundedAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
                            RefundReason = reader.IsDBNull(13) ? null : reader.GetString(13),
                            Discount = reader.GetDecimal(14),
                            PromotionId = reader.IsDBNull(15) ? null : reader.GetString(15)
                        };
                        receipts.Add(receipt);
                        byId[receipt.Id] = receipt;
                    }
                }
            }
            if (receipts.Count == 0) return receipts;
            using (var command = new MySqlCommand(
                "SELECT id_recibo, id_producto, id_variante, nombre, comentario, precio_unitario, cantidad " +
                "FROM recibo_linea " + filter + "ORDER BY id_recibo, orden", connection, transaction))
            {
                if (receiptId != null) command.Parameters.AddWithValue("@id", receiptId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Receipt owner;
                        if (!byId.TryGetValue(reader.GetString(0), out owner)) continue;
                        owner.Lines.Add(new ReceiptLine
                        {
                            ProductId = reader.IsDBNull(1) ? null : reader.GetString(1),
                            OptionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                            Name = reader.GetString(3),
                            Comment = reader.IsDBNull(4) ? null : reader.GetString(4),
                            UnitPrice = reader.GetDecimal(5),
                            Quantity = reader.GetInt32(6)
                        });
                    }
                }
            }
            AttachComprobantes(connection, transaction, receiptId, byId);
            return receipts;
        }

        // Hora local sin fracción de segundo: las columnas DATETIME no guardan fracciones y MySQL redondea en vez de truncar.
        private static DateTime NowWithoutFraction()
        {
            var now = DateTime.Now;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);
        }

        private sealed class StockChange
        {
            public readonly Product Product;
            public readonly ProductOption Option;
            public readonly InventoryMovement Movement;
            public StockChange(Product product, ProductOption option, InventoryMovement movement)
            {
                Product = product; Option = option; Movement = movement;
            }
        }

        private static void Execute(MySqlConnection connection, MySqlTransaction transaction, string sql, Dictionary<string, object> parameters)
        {
            using (var command = new MySqlCommand(sql, connection, transaction))
            {
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static string Clip(string value, int maxLength)
        {
            value = value ?? "";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static List<Product> LoadProductsFromDb()
        {
            using (var connection = SqlConnectionFactory.Open())
                return LoadProductsFromDb(connection, null, false);
        }

        // forUpdate bloquea las filas leídas hasta el final de la transacción (venta y reembolso).
        private static List<Product> LoadProductsFromDb(MySqlConnection connection, MySqlTransaction transaction, bool forUpdate)
        {
            var products = new List<Product>();
            var byId = new Dictionary<string, Product>();
            var lockClause = forUpdate ? " FOR UPDATE" : "";
            {
                using (var command = new MySqlCommand(
                    "SELECT id_producto, categoria, nombre, precio, imagen, color_hex, activo, controla_inventario, stock, stock_inicial, stock_minimo " +
                    "FROM producto ORDER BY categoria, nombre" + lockClause, connection, transaction))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var product = new Product
                        {
                            Id = reader.GetString(0),
                            Category = reader.GetString(1),
                            Name = reader.GetString(2),
                            Price = reader.GetDecimal(3),
                            ImageFile = reader.IsDBNull(4) ? null : reader.GetString(4),
                            ColorHex = reader.GetString(5),
                            Active = reader.GetBoolean(6),
                            TrackInventory = reader.GetBoolean(7),
                            Stock = reader.GetInt32(8),
                            InitialStock = reader.GetInt32(9),
                            StockMinimo = reader.GetInt32(10),
                            Options = new List<ProductOption>()
                        };
                        products.Add(product);
                        byId[product.Id] = product;
                    }
                }
                using (var command = new MySqlCommand(
                    "SELECT id_variante, id_producto, nombre, precio, controla_inventario, stock, stock_inicial, stock_minimo " +
                    "FROM producto_variante ORDER BY id_producto, orden, nombre" + lockClause, connection, transaction))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Product owner;
                        if (!byId.TryGetValue(reader.GetString(1), out owner)) continue;
                        owner.Options.Add(new ProductOption
                        {
                            Id = reader.GetString(0),
                            Name = reader.GetString(2),
                            Price = reader.GetDecimal(3),
                            TrackInventory = reader.GetBoolean(4),
                            Stock = reader.GetInt32(5),
                            InitialStock = reader.GetInt32(6),
                            StockMinimo = reader.GetInt32(7)
                        });
                    }
                }
            }
            return products;
        }

        private static List<InventoryMovement> LoadInventoryMovementsFromDb()
        {
            var movements = new List<InventoryMovement>();
            using (var connection = SqlConnectionFactory.Open())
            using (var command = new MySqlCommand(
                "SELECT id_movimiento, id_producto, id_variante, producto_nombre, tipo, cantidad, stock_previo, stock_nuevo, nota, referencia_id, created_at " +
                "FROM movimiento_inventario ORDER BY secuencia", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // Los movimientos de variante guardan el id de la variante en ProductId (como en el JSON).
                    var variantId = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var productId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    movements.Add(new InventoryMovement
                    {
                        Id = reader.GetString(0),
                        ProductId = variantId ?? productId,
                        ProductName = reader.GetString(3),
                        Type = reader.GetString(4),
                        Quantity = reader.GetInt32(5),
                        PreviousStock = reader.GetInt32(6),
                        NewStock = reader.GetInt32(7),
                        Note = reader.GetString(8),
                        ReferenceId = reader.IsDBNull(9) ? null : reader.GetString(9),
                        CreatedAt = reader.GetDateTime(10)
                    });
                }
            }
            return movements;
        }

        private static void SeedProductsIfEmpty()
        {
            lock (Sync)
            {
                long count;
                using (var connection = SqlConnectionFactory.Open())
                using (var command = new MySqlCommand("SELECT COUNT(*) FROM producto", connection))
                    count = Convert.ToInt64(command.ExecuteScalar());
                if (count > 0) return;
                var products = CreateDefaultProducts();
                foreach (var product in products.Where(x => ProductOptionCatalog.HasTemplate(x.Name)))
                {
                    product.Options = ProductOptionCatalog.CreateDefaultOptions(product.Name);
                    product.Price = product.Options.Min(x => x.Price);
                }
                WriteProducts(products);
            }
        }

        // Sincroniza la lista completa con MySQL en una transacción: borra lo que ya no está, actualiza lo existente
        // e inserta lo nuevo (productos y variantes). Los cambios de precio quedan en la auditoría (misma transacción).
        // checkConflict: compara la huella del servidor (con las filas bloqueadas) contra la última leída por este proceso; sin lectura previa no comprueba.
        private static void WriteProducts(List<Product> products, bool checkConflict = false)
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>();
            var seenOptionIds = new HashSet<string>();
            foreach (var product in products)
            {
                if (product == null || string.IsNullOrWhiteSpace(product.Id)) throw new ArgumentException("Hay un artículo sin identificador.");
                if (string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 60)
                    throw new ArgumentException("El nombre del artículo debe tener entre 1 y 60 caracteres.");
                if ((product.Category ?? "").Length > 40)
                    throw new ArgumentException("La categoría de \"" + product.Name + "\" no puede superar 40 caracteres.");
                if ((product.ImageFile ?? "").Length > 255)
                    throw new ArgumentException("La ruta de imagen de \"" + product.Name + "\" es demasiado larga.");
                if (!seenIds.Add(product.Id)) throw new InvalidOperationException("Hay artículos repetidos en la lista.");
                if (!seenNames.Add((product.Category ?? "") + "\u0001" + product.Name))
                    throw new InvalidOperationException("Ya existe un artículo \"" + product.Name + "\" en la categoría " + (product.Category ?? "") + ".");
                foreach (var option in product.Options ?? new List<ProductOption>())
                {
                    if (option == null || string.IsNullOrWhiteSpace(option.Id)) throw new ArgumentException("Hay una variante sin identificador en \"" + product.Name + "\".");
                    if (string.IsNullOrWhiteSpace(option.Name) || option.Name.Length > 80)
                        throw new ArgumentException("El nombre de la variante de \"" + product.Name + "\" debe tener entre 1 y 80 caracteres.");
                    if (!seenOptionIds.Add(option.Id)) throw new InvalidOperationException("Hay variantes repetidas en \"" + product.Name + "\".");
                }
            }

            try
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    if (checkConflict)
                    {
                        var serverFingerprint = ComputeCatalogFingerprint(connection, transaction, true);
                        if (catalogFingerprint != null && serverFingerprint != catalogFingerprint)
                            throw new InvalidOperationException("El inventario cambió en otra caja. Se recargaron los datos; repita el cambio.");
                    }
                    var existingProducts = new Dictionary<string, decimal>();
                    var existingOptions = new Dictionary<string, decimal>();
                    using (var command = new MySqlCommand("SELECT id_producto, precio FROM producto", connection, transaction))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) existingProducts[reader.GetString(0)] = reader.GetDecimal(1);
                    using (var command = new MySqlCommand("SELECT id_variante, precio FROM producto_variante", connection, transaction))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) existingOptions[reader.GetString(0)] = reader.GetDecimal(1);

                    foreach (var id in existingOptions.Keys.Where(x => !seenOptionIds.Contains(x)))
                        Execute(connection, transaction, "DELETE FROM producto_variante WHERE id_variante = @id", new Dictionary<string, object> { { "@id", id } });
                    foreach (var id in existingProducts.Keys.Where(x => !seenIds.Contains(x)))
                        Execute(connection, transaction, "DELETE FROM producto WHERE id_producto = @id", new Dictionary<string, object> { { "@id", id } });

                    foreach (var product in products)
                    {
                        var values = new Dictionary<string, object>
                        {
                            { "@id", product.Id },
                            { "@categoria", product.Category ?? "" },
                            { "@nombre", product.Name },
                            { "@precio", Math.Round(product.Price, 2) },
                            { "@imagen", string.IsNullOrEmpty(product.ImageFile) ? null : product.ImageFile },
                            { "@color", string.IsNullOrWhiteSpace(product.ColorHex) || product.ColorHex.Length > 7 ? "#808080" : product.ColorHex },
                            { "@activo", product.Active },
                            { "@controla", product.TrackInventory },
                            { "@stock", product.Stock },
                            { "@inicial", product.InitialStock },
                            { "@minimo", Math.Max(0, product.StockMinimo) }
                        };
                        decimal previousPrice;
                        if (existingProducts.TryGetValue(product.Id, out previousPrice))
                        {
                            // Con variantes el precio del artículo es derivado (el mínimo de ellas): el cambio se audita en la variante.
                            var newPrice = Math.Round(product.Price, 2);
                            if (newPrice != previousPrice && (product.Options == null || product.Options.Count == 0))
                                RegistrarAuditoria(connection, transaction, AuditActions.PrecioCambiado,
                                    "Artículo " + product.Name + ": " + previousPrice.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + newPrice.ToString("0.00", CultureInfo.InvariantCulture));
                            // stock y stock_inicial de filas existentes NO se escriben aquí: solo cambian por entradas, ventas y reembolsos (PersistStockChanges).
                            Execute(connection, transaction,
                                "UPDATE producto SET categoria = @categoria, nombre = @nombre, precio = @precio, imagen = @imagen, color_hex = @color, " +
                                "activo = @activo, controla_inventario = @controla, stock_minimo = @minimo WHERE id_producto = @id", values);
                        }
                        else
                            Execute(connection, transaction,
                                "INSERT INTO producto (id_producto, categoria, nombre, precio, imagen, color_hex, activo, controla_inventario, stock, stock_inicial, stock_minimo) " +
                                "VALUES (@id, @categoria, @nombre, @precio, @imagen, @color, @activo, @controla, @stock, @inicial, @minimo)", values);
                    }

                    foreach (var product in products)
                    {
                        // 'orden' guarda la posición de la variante en Product.Options para devolverlas como el usuario las creó.
                        var optionOrder = 0;
                        foreach (var option in product.Options ?? new List<ProductOption>())
                        {
                            var values = new Dictionary<string, object>
                            {
                                { "@id", option.Id },
                                { "@producto", product.Id },
                                { "@nombre", option.Name },
                                { "@orden", optionOrder++ },
                                { "@precio", Math.Round(option.Price, 2) },
                                { "@controla", option.TrackInventory },
                                { "@stock", option.Stock },
                                { "@inicial", option.InitialStock },
                                { "@minimo", Math.Max(0, option.StockMinimo) }
                            };
                            decimal previousOptionPrice;
                            if (existingOptions.TryGetValue(option.Id, out previousOptionPrice))
                            {
                                var newOptionPrice = Math.Round(option.Price, 2);
                                if (newOptionPrice != previousOptionPrice)
                                    RegistrarAuditoria(connection, transaction, AuditActions.PrecioCambiado,
                                        "Artículo " + product.Name + ", variante " + option.Name + ": " + previousOptionPrice.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + newOptionPrice.ToString("0.00", CultureInfo.InvariantCulture));
                                Execute(connection, transaction,
                                    "UPDATE producto_variante SET id_producto = @producto, nombre = @nombre, orden = @orden, precio = @precio, controla_inventario = @controla, " +
                                    "stock_minimo = @minimo WHERE id_variante = @id", values);
                            }
                            else
                                Execute(connection, transaction,
                                    "INSERT INTO producto_variante (id_variante, id_producto, nombre, orden, precio, controla_inventario, stock, stock_inicial, stock_minimo) " +
                                    "VALUES (@id, @producto, @nombre, @orden, @precio, @controla, @stock, @inicial, @minimo)", values);
                        }
                    }
                    var writtenFingerprint = ComputeCatalogFingerprint(connection, transaction, false);
                    transaction.Commit();
                    catalogFingerprint = writtenFingerprint;
                }
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                throw new InvalidOperationException("Ya existe otro artículo con el mismo nombre y categoría.", ex);
            }
        }

        // Guarda el stock resultante y los movimientos nuevos en una sola transacción.
        private static void PersistStockChanges(List<StockChange> changes)
        {
            if (changes == null || changes.Count == 0) return;
            using (var connection = SqlConnectionFactory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                PersistStockChanges(connection, transaction, changes);
                transaction.Commit();
            }
        }

        // Misma lógica dentro de una transacción ya abierta: el llamador decide el Commit (venta y reembolso).
        private static void PersistStockChanges(MySqlConnection connection, MySqlTransaction transaction, List<StockChange> changes)
        {
            if (changes == null || changes.Count == 0) return;
            {
                foreach (var change in changes)
                {
                    if (change.Option != null)
                        Execute(connection, transaction,
                            "UPDATE producto_variante SET controla_inventario = @controla, stock = @stock, stock_inicial = @inicial WHERE id_variante = @id",
                            new Dictionary<string, object>
                            {
                                { "@id", change.Option.Id }, { "@controla", change.Option.TrackInventory },
                                { "@stock", change.Option.Stock }, { "@inicial", change.Option.InitialStock }
                            });
                    else
                        Execute(connection, transaction,
                            "UPDATE producto SET controla_inventario = @controla, stock = @stock, stock_inicial = @inicial WHERE id_producto = @id",
                            new Dictionary<string, object>
                            {
                                { "@id", change.Product.Id }, { "@controla", change.Product.TrackInventory },
                                { "@stock", change.Product.Stock }, { "@inicial", change.Product.InitialStock }
                            });
                    var movement = change.Movement;
                    Execute(connection, transaction,
                        "INSERT INTO movimiento_inventario (id_movimiento, id_producto, id_variante, producto_nombre, tipo, cantidad, stock_previo, stock_nuevo, nota, referencia_id, created_at) " +
                        "VALUES (@id, @producto, @variante, @nombre, @tipo, @cantidad, @previo, @nuevo, @nota, @referencia, @fecha)",
                        new Dictionary<string, object>
                        {
                            { "@id", movement.Id }, { "@producto", change.Product.Id },
                            { "@variante", change.Option == null ? null : change.Option.Id },
                            { "@nombre", Clip(movement.ProductName, 120) }, { "@tipo", Clip(movement.Type, 20) },
                            { "@cantidad", movement.Quantity }, { "@previo", movement.PreviousStock }, { "@nuevo", movement.NewStock },
                            { "@nota", Clip(movement.Note, 150) }, { "@referencia", movement.ReferenceId },
                            { "@fecha", movement.CreatedAt }
                        });
                }
            }
        }

        private static InventoryMovement NewInventoryMovement(Product product, string type, int quantity, int previous, string note, string referenceId)
        {
            return new InventoryMovement
            {
                Id = Guid.NewGuid().ToString("N"), ProductId = product.Id, ProductName = product.Name,
                CreatedAt = DateTime.Now, Type = type, Quantity = quantity, PreviousStock = previous,
                NewStock = product.Stock, Note = string.IsNullOrWhiteSpace(note) ? "Sin comentario" : note.Trim(), ReferenceId = referenceId
            };
        }

        private static InventoryMovement NewInventoryMovement(Product product, ProductOption option, string type, int quantity, int previous, string note, string referenceId)
        {
            return new InventoryMovement
            {
                Id = Guid.NewGuid().ToString("N"), ProductId = option.Id, ProductName = product.Name + " · " + option.Name,
                CreatedAt = DateTime.Now, Type = type, Quantity = quantity, PreviousStock = previous,
                NewStock = option.Stock, Note = string.IsNullOrWhiteSpace(note) ? "Sin comentario" : note.Trim(), ReferenceId = referenceId
            };
        }

        private static ProductOption FindLineOption(Product product, ReceiptLine line)
        {
            if (product == null || line == null || product.Options == null) return null;
            if (!string.IsNullOrWhiteSpace(line.OptionId))
                return product.Options.FirstOrDefault(x => x.Id == line.OptionId);
            return product.Options.FirstOrDefault(x =>
                string.Equals(product.Name + " · " + x.Name, line.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        public static Shift OpenNewShift(decimal openingAmount)
        {
            lock (Sync)
            {
                Shift shift;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    if (ReadOpenShift(connection, transaction) != null) throw new InvalidOperationException("Ya existe un turno abierto.");
                    int number;
                    string defaultEmployee;
                    // FOR UPDATE: el número de turno y su incremento van en la misma transacción que el INSERT.
                    using (var command = new MySqlCommand("SELECT next_shift_number, employee_name FROM configuracion WHERE id = 1 FOR UPDATE", connection, transaction))
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("La configuración del negocio no está inicializada.");
                        number = reader.GetInt32(0);
                        defaultEmployee = reader.GetString(1);
                    }
                    var employee = string.IsNullOrWhiteSpace(AppSession.UserName) ? defaultEmployee : AppSession.UserName;
                    shift = new Shift
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Number = number,
                        Employee = Clip(employee, 60),
                        OpenedAt = NowWithoutFraction(),
                        OpeningAmount = Math.Round(openingAmount, 2),
                        IsOpen = true
                    };
                    Execute(connection, transaction,
                        "INSERT INTO sesion_caja (id_sesion, numero, id_usuario, empleado, fecha_apertura, monto_inicial, abierta) " +
                        "VALUES (@id, @numero, @usuario, @empleado, @apertura, @inicial, 1)",
                        new Dictionary<string, object>
                        {
                            { "@id", shift.Id },
                            { "@numero", shift.Number },
                            { "@usuario", FindActiveUserId(connection, transaction, shift.Employee) },
                            { "@empleado", shift.Employee },
                            { "@apertura", shift.OpenedAt },
                            { "@inicial", shift.OpeningAmount }
                        });
                    Execute(connection, transaction, "UPDATE configuracion SET next_shift_number = @siguiente WHERE id = 1",
                        new Dictionary<string, object> { { "@siguiente", number + 1 } });
                    transaction.Commit();
                }
                RaiseChanged();
                return shift;
            }
        }

        public static void CloseShift(string id, decimal closingAmount)
        {
            lock (Sync)
            {
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var shift = ReadShifts(connection, transaction, "WHERE id_sesion = @id AND abierta = 1 FOR UPDATE", new Dictionary<string, object> { { "@id", id ?? "" } }).FirstOrDefault();
                    if (shift == null) return;
                    shift.ExpectedAmount = CalculateExpectedCash(connection, transaction, shift);
                    shift.ClosingAmount = Math.Round(closingAmount, 2);
                    shift.ClosedAt = NowWithoutFraction();
                    shift.IsOpen = false;
                    Execute(connection, transaction,
                        "UPDATE sesion_caja SET fecha_cierre = @cierre, monto_cierre = @monto, monto_esperado = @esperado, abierta = 0 WHERE id_sesion = @id",
                        new Dictionary<string, object>
                        {
                            { "@id", shift.Id },
                            { "@cierre", shift.ClosedAt.Value },
                            { "@monto", shift.ClosingAmount },
                            { "@esperado", Math.Round(shift.ExpectedAmount, 2) }
                        });
                    var difference = Math.Round(shift.ClosingAmount, 2) - Math.Round(shift.ExpectedAmount, 2);
                    if (difference != 0m)
                        RegistrarAuditoria(connection, transaction, AuditActions.CajaDiferencia,
                            "Turno #" + shift.Number + ": esperado " + Math.Round(shift.ExpectedAmount, 2).ToString("0.00", CultureInfo.InvariantCulture) +
                            ", contado " + Math.Round(shift.ClosingAmount, 2).ToString("0.00", CultureInfo.InvariantCulture) +
                            ", diferencia " + difference.ToString("0.00", CultureInfo.InvariantCulture));
                    transaction.Commit();
                }
                RaiseChanged();
            }
        }

        public static CashMovement AddCashMovement(string type, decimal amount, string comment)
        {
            lock (Sync)
            {
                CashMovement movement;
                using (var connection = SqlConnectionFactory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var shift = ReadOpenShift(connection, transaction);
                    if (shift == null) throw new InvalidOperationException("Abra un turno antes de registrar movimientos de tesorería.");
                    string defaultEmployee;
                    using (var command = new MySqlCommand("SELECT employee_name FROM configuracion WHERE id = 1", connection, transaction))
                    {
                        var result = command.ExecuteScalar();
                        defaultEmployee = result == null || result == DBNull.Value ? "" : Convert.ToString(result);
                    }
                    var employee = string.IsNullOrWhiteSpace(AppSession.UserName) ? defaultEmployee : AppSession.UserName;
                    movement = new CashMovement
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ShiftId = shift.Id,
                        CreatedAt = NowWithoutFraction(),
                        Employee = Clip(employee, 60),
                        Type = Clip(type, 10),
                        Amount = type == "Salida" ? -Math.Abs(Math.Round(amount, 2)) : Math.Abs(Math.Round(amount, 2)),
                        Comment = Clip(string.IsNullOrWhiteSpace(comment) ? "Sin comentario" : comment.Trim(), 150)
                    };
                    Execute(connection, transaction,
                        "INSERT INTO movimiento_caja (id_movimiento, id_sesion, id_usuario, empleado, tipo, importe, comentario, created_at) " +
                        "VALUES (@id, @sesion, @usuario, @empleado, @tipo, @importe, @comentario, @fecha)",
                        new Dictionary<string, object>
                        {
                            { "@id", movement.Id },
                            { "@sesion", movement.ShiftId },
                            { "@usuario", FindActiveUserId(connection, transaction, movement.Employee) },
                            { "@empleado", movement.Employee },
                            { "@tipo", movement.Type },
                            { "@importe", movement.Amount },
                            { "@comentario", movement.Comment },
                            { "@fecha", movement.CreatedAt }
                        });
                    transaction.Commit();
                }
                RaiseChanged();
                return movement;
            }
        }

        public static decimal CalculateExpectedCash(Shift shift)
        {
            if (shift == null) return 0m;
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
                return CalculateExpectedCash(connection, null, shift);
        }

        // Efectivo cobrado = suma SQL con la misma definición que Receipt.CashPaid (Mixto: monto_efectivo; Efectivo: total; Yape: 0),
        // solo recibos Pagado dentro del rango del turno (hora local con hora local). Los movimientos de caja, de MySQL.
        private static decimal CalculateExpectedCash(MySqlConnection connection, MySqlTransaction transaction, Shift shift)
        {
            decimal cashSales;
            using (var command = new MySqlCommand(
                "SELECT COALESCE(SUM(CASE medio_pago WHEN 'Mixto' THEN monto_efectivo WHEN 'Efectivo' THEN total ELSE 0 END), 0) FROM recibo " +
                "WHERE estado = 'Pagado' AND created_at >= @desde" + (shift.ClosedAt.HasValue ? " AND created_at <= @hasta" : ""), connection, transaction))
            {
                command.Parameters.AddWithValue("@desde", shift.OpenedAt);
                if (shift.ClosedAt.HasValue) command.Parameters.AddWithValue("@hasta", shift.ClosedAt.Value);
                cashSales = Convert.ToDecimal(command.ExecuteScalar());
            }
            decimal movementTotal;
            using (var command = new MySqlCommand("SELECT COALESCE(SUM(importe), 0) FROM movimiento_caja WHERE id_sesion = @id", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", shift.Id);
                movementTotal = Convert.ToDecimal(command.ExecuteScalar());
            }
            return shift.OpeningAmount + cashSales + movementTotal;
        }

        private const string ShiftColumns =
            "SELECT id_sesion, numero, empleado, fecha_apertura, fecha_cierre, monto_inicial, monto_cierre, monto_esperado, abierta FROM sesion_caja ";

        private static List<Shift> ReadShifts(MySqlConnection connection, MySqlTransaction transaction, string clause, Dictionary<string, object> parameters)
        {
            var shifts = new List<Shift>();
            using (var command = new MySqlCommand(ShiftColumns + clause, connection, transaction))
            {
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        shifts.Add(new Shift
                        {
                            Id = reader.GetString(0),
                            Number = reader.GetInt32(1),
                            Employee = reader.GetString(2),
                            OpenedAt = reader.GetDateTime(3),
                            ClosedAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
                            OpeningAmount = reader.GetDecimal(5),
                            ClosingAmount = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
                            ExpectedAmount = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7),
                            IsOpen = reader.GetBoolean(8)
                        });
                    }
                }
            }
            return shifts;
        }

        private static Shift ReadOpenShift(MySqlConnection connection, MySqlTransaction transaction)
        {
            return ReadShifts(connection, transaction, "WHERE abierta = 1 ORDER BY fecha_apertura DESC LIMIT 1", new Dictionary<string, object>()).FirstOrDefault();
        }

        // id del usuario activo con ese nombre (NULL si no existe): la caja guarda además el nombre en texto.
        private static object FindActiveUserId(MySqlConnection connection, MySqlTransaction transaction, string name)
        {
            using (var command = new MySqlCommand("SELECT id_usuario FROM usuario WHERE nombre = @nombre AND activo = 1 ORDER BY id_usuario LIMIT 1", connection, transaction))
            {
                command.Parameters.AddWithValue("@nombre", name ?? "");
                var result = command.ExecuteScalar();
                return result == null || result == DBNull.Value ? null : result;
            }
        }

        public static string Money(decimal value)
        {
            return CurrencySymbol() + value.ToString("N2", CultureInfo.GetCultureInfo("es-PE"));
        }

        // El símbolo se cachea: Money se llama por cada fila de las pantallas y no debe consultar MySQL cada vez.
        private static string CurrencySymbol()
        {
            var cached = currencySymbolCache;
            if (cached != null) return cached;
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                currencySymbolCache = ReadConfiguration(connection, null).CurrencySymbol;
                return currencySymbolCache;
            }
        }

        public static string ResolveAsset(string imageFile)
        {
            if (string.IsNullOrWhiteSpace(imageFile)) return null;
            if (imageFile.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(DataDirectory, "Images", imageFile.Substring(5));
            if (Path.IsPathRooted(imageFile)) return imageFile;
            return Path.Combine(AssetDirectory, imageFile);
        }

        public static Image LoadImage(string imageFile)
        {
            try
            {
                var path = ResolveAsset(imageFile);
                if (!File.Exists(path)) return null;
                using (var stream = File.OpenRead(path))
                using (var source = Image.FromStream(stream))
                    return new Bitmap(source);
            }
            catch { return null; }
        }

        public static void OpenDataFolder()
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DataDirectory + "\"") { UseShellExecute = true });
        }

        public static void SetPortableMode(bool enabled)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDENPOS_DATA_PATH")))
                throw new InvalidOperationException("No se puede cambiar el modo portable mientras EDENPOS_DATA_PATH está configurado.");
            var marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.mode");
            var portableData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BaseDatos");
            var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdenPOS", "Data");
            if (enabled)
            {
                if (IsPortableMode) return;
                var currentData = DataDirectory;
                EnsureDirectoryWritable(AppDomain.CurrentDomain.BaseDirectory);
                CopyDirectory(currentData, portableData);
                File.WriteAllText(marker, "Edén POS portable mode", Encoding.UTF8);
            }
            else
            {
                if (!IsPortableMode) return;
                CopyDirectory(portableData, localData);
                File.Delete(marker);
            }
            Directory.CreateDirectory(DataDirectory);
            Initialize();
            RaiseChanged();
        }

        // ---- Copia de seguridad (formato 2: una tabla de MySQL por archivo JSON dentro de un zip) ----
        // Orden fijo según las llaves foráneas (padres primero). Es la única fuente de nombres de tabla para la copia.
        private static readonly string[] BackupTables =
        {
            "rol", "usuario", "configuracion", "cliente", "producto", "producto_variante", "movimiento_inventario",
            "ticket_guardado", "ticket_linea", "casillero_llave", "sesion_caja", "movimiento_caja", "recibo",
            "recibo_linea", "comprobante", "auditoria"
        };
        private const int BackupFormatVersion = 2;
        private const string BackupDateFormat = "yyyy-MM-ddTHH:mm:ss";
        private const string BackupManifestName = "backup_manifest.json";
        private const string BackupFilesPrefix = "files/";
        private const string BackupTablesPrefix = "tables/";
        // Límites de lectura/escritura de una copia. Los de importación se comprueban con los bytes realmente descomprimidos.
        private const long BackupMaxTableBytes = 512L * 1024 * 1024;      // tables/*.json y backup_manifest.json (y los JSON de copias antiguas)
        private const long BackupMaxFileBytes = 20L * 1024 * 1024;        // cada archivo de files/ (imágenes)
        private const long BackupMaxTotalBytes = 2048L * 1024 * 1024;     // total descomprimido
        private const int BackupMaxEntries = 20000;

        // Se lanza al cobrar un ticket guardado que otra caja ya cobró o eliminó (hereda de InvalidOperationException).
        public sealed class SavedTicketUnavailableException : InvalidOperationException
        {
            public SavedTicketUnavailableException() : base("Este ticket ya se cobró o se eliminó en otra caja. Actualice la lista.") { }
        }

        // Se lanza cuando los datos YA se restauraron (commit hecho) pero falló un paso posterior (imágenes, avisos, ajustes).
        // La base quedó con los datos de la copia: la interfaz no debe decir que la importación falló.
        public sealed class BackupPostRestoreException : Exception
        {
            public int RestoredRows { get; private set; }
            public BackupPostRestoreException(int restoredRows, string message, Exception inner) : base(message, inner) { RestoredRows = restoredRows; }
        }

        // Avisos de la última restauración que sí se completó (sesión cerrada o reiniciada, dueña predeterminada creada...).
        private static List<string> lastRestoreNotes = new List<string>();
        public static IList<string> LastRestoreNotes { get { lock (Sync) return lastRestoreNotes.ToList(); } }

        // Devuelve el total de filas exportadas.
        public static int ExportBackup(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Seleccione un archivo de destino.");
            Directory.CreateDirectory(DataDirectory);
            var destination = Path.GetFullPath(destinationPath);
            var dataRoot = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Guarde la copia fuera de la carpeta de datos de Edén POS.");

            var temporary = destination + ".tmp";
            var counts = new Dictionary<string, int>();
            var fileCount = 0;
            // Sin lock (Sync) durante la exportación: la lectura de MySQL usa su propia conexión con un snapshot consistente, así que las ventas
            // y la interfaz siguen respondiendo mientras se copia. Solo la enumeración de la carpeta de datos va protegida, en un tramo corto.
            try
            {
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                using (var connection = SqlConnectionFactory.Open())
                {
                    // Una sola lectura consistente: todas las tablas se ven como en el mismo instante.
                    RunSql(connection, "START TRANSACTION WITH CONSISTENT SNAPSHOT");
                    try
                    {
                        foreach (var table in BackupTables)
                            counts[table] = WriteTableToArchive(connection, archive, table);
                    }
                    finally
                    {
                        try { RunSql(connection, "ROLLBACK"); } catch { }
                    }

                    // Imágenes y demás archivos de la carpeta de datos (nunca settings.json, el registro de errores ni temporales).
                    string[] sources;
                    lock (Sync) sources = Directory.GetFiles(DataDirectory, "*", SearchOption.AllDirectories);
                    foreach (var source in sources)
                    {
                        var relative = source.Substring(dataRoot.Length).Replace('\\', '/');
                        if (relative.IndexOf('/') < 0 && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue; // JSON antiguos de nivel superior (incluye settings.json)
                        if (relative.Equals("errors.log", StringComparison.OrdinalIgnoreCase) ||
                            relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            relative.IndexOf(".invalid-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        try
                        {
                            if (new FileInfo(source).Length > BackupMaxFileBytes)
                                throw new InvalidOperationException("El archivo \"" + relative + "\" supera los 20 MB y no se podría restaurar. Quítelo de la carpeta de datos o redúzcalo. No se creó la copia.");
                            if (fileCount + BackupTables.Length + 1 > BackupMaxEntries)
                                throw new InvalidOperationException("La carpeta de datos tiene demasiados archivos para una copia (máximo " + BackupMaxEntries + "). No se creó la copia.");
                            archive.CreateEntryFromFile(source, BackupFilesPrefix + relative, CompressionLevel.Optimal);
                            fileCount++;
                        }
                        catch (FileNotFoundException) { } // el archivo se eliminó mientras se copiaba
                        catch (DirectoryNotFoundException) { }
                    }

                    WriteZipText(archive, BackupManifestName, PrettyJson(Json.Serialize(new Dictionary<string, object>
                    {
                        { "application", "EdenPOS" },
                        { "format_version", BackupFormatVersion },
                        { "app_version", typeof(AppData).Assembly.GetName().Version.ToString() },
                        { "created_at", DateTime.Now.ToString(BackupDateFormat, CultureInfo.InvariantCulture) },
                        { "token_included", false },
                        { "tables", counts },
                        { "file_count", fileCount }
                    })));
                }
                File.Copy(temporary, destination, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return counts.Values.Sum();
        }

        // Escribe una tabla como entrada del zip fila por fila (sin acumular todo el JSON en memoria). El formato es el mismo de
        // Json.Serialize(List<Dictionary>): un arreglo JSON de objetos. Devuelve el número de filas.
        private static int WriteTableToArchive(MySqlConnection connection, ZipArchive archive, string table)
        {
            var entry = archive.CreateEntry(BackupTablesPrefix + table + ".json", CompressionLevel.Optimal);
            var utf8 = new UTF8Encoding(false);
            long bytes = 2;
            var count = 0;
            using (var output = new StreamWriter(entry.Open(), utf8))
            using (var command = new MySqlCommand("SELECT * FROM `" + table + "`", connection))
            using (var reader = command.ExecuteReader())
            {
                output.Write('[');
                while (reader.Read())
                {
                    var row = new Dictionary<string, object>();
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        var name = reader.GetName(index);
                        var value = reader.GetValue(index);
                        if (table == "configuracion" && name == "dni_api_token_protected") value = "";
                        else if (table == "configuracion" && name == "dni_lookup_enabled") value = false;
                        row[name] = BackupValue(value);
                    }
                    var json = Json.Serialize(row);
                    bytes += utf8.GetByteCount(json) + 1;
                    if (bytes > BackupMaxTableBytes)
                        throw new InvalidOperationException("La tabla " + table + " es demasiado grande para incluirla en una copia (más de 512 MB). No se creó la copia.");
                    if (count > 0) output.Write(',');
                    output.Write(json);
                    count++;
                }
                output.Write(']');
            }
            return count;
        }

        private static void RunSql(MySqlConnection connection, string sql)
        {
            using (var command = new MySqlCommand(sql, connection)) command.ExecuteNonQuery();
        }

        private static object BackupValue(object value)
        {
            if (value == null || value is DBNull) return null;
            if (value is DateTime) return ((DateTime)value).ToString(BackupDateFormat, CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            if (value is bool || value is string) return value;
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // Restaura una copia de formato 2. Devuelve el total de filas restauradas. Todo o nada: ante cualquier fallo la base queda como estaba.
        public static int ImportBackup(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("No se encontró la copia seleccionada.", sourcePath);

            var entries = ReadBackupEntries(sourcePath);

            // Validación completa ANTES de tocar la base.
            byte[] manifestBytes;
            Dictionary<string, object> manifest = null;
            if (entries.TryGetValue(BackupManifestName, out manifestBytes))
            {
                try { manifest = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(manifestBytes)); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { manifest = null; }
            }
            object versionValue = null;
            if (manifest == null || !manifest.TryGetValue("format_version", out versionValue))
            {
                // Copia de la versión anterior (JSON en la raíz del zip): se importa reemplazando los datos actuales.
                if (entries.ContainsKey("catalog.json") || entries.ContainsKey("settings.json"))
                    return ImportLegacyBackup(entries);
                throw new InvalidDataException("El archivo no es una copia válida de Edén POS.");
            }
            int version;
            if (!int.TryParse(Convert.ToString(versionValue, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version < 1)
                throw new InvalidDataException("El manifiesto de la copia no es válido.");
            if (version > BackupFormatVersion)
                throw new InvalidDataException("La copia fue creada con una versión más reciente de Edén POS y no se puede restaurar aquí.");
            if (version < BackupFormatVersion)
                throw new InvalidDataException("Esta copia es de la versión anterior basada en archivos. Use la importación de datos antiguos.");

            var tables = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var table in BackupTables)
            {
                byte[] bytes;
                if (!entries.TryGetValue(BackupTablesPrefix + table + ".json", out bytes))
                    throw new InvalidDataException("La copia está incompleta: falta la tabla " + table + ".");
                List<Dictionary<string, object>> rows;
                try { rows = Json.Deserialize<List<Dictionary<string, object>>>(Encoding.UTF8.GetString(bytes)); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    throw new InvalidDataException("La tabla " + table + " de la copia no es válida.", ex);
                }
                if (rows == null || rows.Any(x => x == null)) throw new InvalidDataException("La tabla " + table + " de la copia no es válida.");
                tables[table] = rows;
            }

            var restoredFiles = new List<KeyValuePair<string, byte[]>>();
            foreach (var item in entries)
            {
                if (!item.Key.StartsWith(BackupFilesPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var destination = ResolveInsideDataDirectory(item.Key.Substring(BackupFilesPrefix.Length));
                var fileName = Path.GetFileName(destination);
                if (fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) continue; // nunca se restaura la configuración local
                restoredFiles.Add(new KeyValuePair<string, byte[]>(destination, item.Value));
            }

            int totalRows;
            lock (Sync)
            {
                // El estado local (token de DNI) no viene de la copia: la búsqueda de DNI solo queda activa si ya hay token aquí.
                var local = LoadLocalSettings();
                var lookupEnabled = !string.IsNullOrWhiteSpace(UnprotectSecret(local.DniApiTokenProtected));
                using (var connection = SqlConnectionFactory.Open())
                    totalRows = RestoreTables(connection, tables, lookupEnabled);
            }

            // A partir de aquí la base YA tiene los datos de la copia: cualquier fallo se informa como "restaurado, pero...".
            var notes = new List<string>();
            var warnings = new List<string>();
            lock (Sync)
            {
                try
                {
                    if (MigrateDefaults()) notes.Add(DefaultOwnerNote);
                }
                catch (Exception ex) { LogError(ex, "Completar datos base tras restaurar la copia"); warnings.Add("no se pudieron completar los datos base (" + DescribeAnyError(ex) + ")"); }
                try
                {
                    using (var connection = SqlConnectionFactory.Open()) AdjustBackupAutoIncrement(connection);
                }
                catch (Exception ex) { LogError(ex, "Ajustar contadores tras restaurar la copia"); warnings.Add("no se pudieron ajustar los contadores de identificadores (" + DescribeAnyError(ex) + ")"); }
            }
            return FinishRestore(totalRows, restoredFiles, notes, warnings);
        }

        private const string DefaultOwnerNote = "No había ninguna dueña activa en los datos restaurados: se creó el usuario DUEÑA con el PIN 0000. Cámbielo cuanto antes.";

        // Pasos posteriores al commit comunes a la copia nueva y a la antigua: sesión, imágenes y pantallas. Nunca deja escapar un fallo
        // como si la restauración no hubiera ocurrido: los reúne en una BackupPostRestoreException.
        private static int FinishRestore(int totalRows, List<KeyValuePair<string, byte[]>> restoredFiles, List<string> notes, List<string> warnings)
        {
            lock (Sync)
            {
                try { RefreshSessionAfterRestore(notes); }
                catch (Exception ex)
                {
                    LogError(ex, "Actualizar la sesión tras restaurar la copia");
                    AppSession.SignOut();
                    notes.Add("No se pudo comprobar su usuario en los datos restaurados, así que debe iniciar sesión de nuevo.");
                }
            }
            try
            {
                foreach (var item in restoredFiles)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Key));
                    File.WriteAllBytes(item.Key, item.Value);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "Restaurar imágenes de la copia");
                warnings.Add("no se pudieron copiar todas las imágenes (" + DescribeAnyError(ex) + ")");
            }
            try { RaiseChanged(); }
            catch (Exception ex) { LogError(ex, "Actualizar pantallas tras restaurar la copia"); warnings.Add("no se pudieron actualizar las pantallas (" + DescribeAnyError(ex) + ")"); }

            lock (Sync) lastRestoreNotes = notes;
            if (warnings.Count > 0)
                throw new BackupPostRestoreException(totalRows, "Los datos se restauraron, pero " + string.Join("; ", warnings) + ".", null);
            return totalRows;
        }

        private static string DescribeAnyError(Exception ex)
        {
            var mysql = ex as MySqlException;
            return mysql != null ? DescribeDatabaseError(mysql) : ex.Message;
        }

        // Lee el zip a memoria con límites: la suma se hace con los bytes realmente descomprimidos (la cabecera del zip puede mentir).
        // Solo se leen las entradas que la app reconoce; el resto se ignora.
        private static Dictionary<string, byte[]> ReadBackupEntries(string sourcePath)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;
            try
            {
                using (var file = File.OpenRead(sourcePath))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count > BackupMaxEntries) throw new InvalidDataException("La copia tiene demasiados archivos.");
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                        var relative = entry.FullName.Replace('\\', '/');
                        bool unsafePath;
                        try { unsafePath = relative.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Split('/').Any(x => x == ".."); }
                        catch (ArgumentException) { throw new InvalidDataException("La copia contiene un nombre de archivo no válido."); }
                        if (unsafePath) throw new InvalidDataException("La copia contiene una ruta no segura.");
                        var limit = BackupEntryLimit(relative);
                        if (limit < 0) continue;
                        if (entry.Length > limit) throw new InvalidDataException("La copia contiene un archivo demasiado grande.");
                        using (var input = entry.Open())
                        using (var memory = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            int read;
                            long actual = 0;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                actual += read;
                                totalSize += read;
                                if (actual > limit) throw new InvalidDataException("La copia contiene un archivo demasiado grande.");
                                if (totalSize > BackupMaxTotalBytes) throw new InvalidDataException("La copia supera el tamaño permitido.");
                                memory.Write(buffer, 0, read);
                            }
                            entries[relative] = memory.ToArray();
                        }
                    }
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // ZipArchive valida los nombres al leer el directorio central y lanza ArgumentException con caracteres no válidos.
                throw new InvalidDataException("La copia contiene un nombre de archivo no válido.", ex);
            }
            catch (IOException ex) { throw new InvalidDataException("El archivo no es una copia válida de Edén POS.", ex); }
            return entries;
        }

        // Tamaño máximo de una entrada del zip según su tipo; -1 = entrada desconocida (se ignora sin leerla).
        private static long BackupEntryLimit(string relative)
        {
            if (relative.Equals(BackupManifestName, StringComparison.OrdinalIgnoreCase)) return BackupMaxTableBytes;
            if (relative.StartsWith(BackupTablesPrefix, StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return BackupMaxTableBytes;
            if (relative.IndexOf('/') < 0 && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return BackupMaxTableBytes; // JSON de copias antiguas
            if (relative.StartsWith(BackupFilesPrefix, StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Images/", StringComparison.OrdinalIgnoreCase)) return BackupMaxFileBytes;
            return -1;
        }

        // Ruta absoluta dentro de la carpeta de datos para una ruta relativa del zip; rechaza nombres inválidos y rutas que se salgan de ella.
        private static string ResolveInsideDataDirectory(string relativePath)
        {
            try
            {
                var dataRoot = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var destination = Path.GetFullPath(Path.Combine(DataDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta inválida dentro de la copia.");
                Path.GetFileName(destination); // lanza ArgumentException con caracteres no válidos en el nombre
                return destination;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new InvalidDataException("La copia contiene un nombre de archivo no válido.", ex);
            }
        }

        // Si el usuario de la sesión ya no existe o su rol cambió, la sesión se cierra (la barra de MainForm se construye una sola vez).
        // Si el id cambió pero hay un usuario activo con el mismo nombre (copias antiguas: los ids se renumeran), se vuelve a iniciar sesión con él.
        private static void RefreshSessionAfterRestore(List<string> notes)
        {
            var current = AppSession.CurrentUser;
            if (current != null)
            {
                var users = Users;
                var match = users.FirstOrDefault(x => x.Id == current.Id && x.Active && string.Equals(x.Name, current.Name, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(x => x.Active && string.Equals(x.Name, current.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    AppSession.SignOut();
                    notes.Add("Su usuario ya no existe (o está inactivo) en esos datos, así que debe iniciar sesión de nuevo.");
                }
                else if (match.Role != current.Role)
                {
                    AppSession.SignOut();
                    notes.Add("El rol de su usuario es distinto en esos datos, así que debe iniciar sesión de nuevo.");
                }
                else AppSession.SignIn(match);
            }
            currencySymbolCache = null;
        }

        // Una transacción: vacía las tablas y las vuelve a llenar con los mismos ids (llaves foráneas activas).
        private static int RestoreTables(MySqlConnection connection, Dictionary<string, List<Dictionary<string, object>>> tables, bool lookupEnabled)
        {
            // Preparación en memoria (columnas validadas contra SHOW COLUMNS y valores convertidos) antes de borrar nada.
            var prepared = new Dictionary<string, KeyValuePair<List<string>, List<object[]>>>();
            foreach (var table in BackupTables)
            {
                var types = ReadColumnTypes(connection, table);
                var rows = tables[table];
                var columns = new List<string>();
                var values = new List<object[]>();
                foreach (var row in rows)
                {
                    if (columns.Count == 0)
                    {
                        columns.AddRange(row.Keys);
                        if (columns.Count == 0) throw new InvalidDataException("Una fila de la tabla " + table + " está vacía.");
                        foreach (var column in columns)
                            if (!types.ContainsKey(column))
                                throw new InvalidDataException("La tabla " + table + " de la copia tiene una columna desconocida: " + column + ".");
                    }
                    else if (row.Count != columns.Count || columns.Any(x => !row.ContainsKey(x)))
                        throw new InvalidDataException("Las filas de la tabla " + table + " no tienen las mismas columnas.");

                    var converted = new object[columns.Count];
                    for (var index = 0; index < columns.Count; index++)
                    {
                        var column = columns[index];
                        var value = row[column];
                        if (table == "configuracion" && column == "dni_api_token_protected") value = "";
                        else if (table == "configuracion" && column == "dni_lookup_enabled") value = lookupEnabled;
                        converted[index] = ConvertBackupValue(value, types[column], table, column);
                    }
                    values.Add(converted);
                }
                prepared[table] = new KeyValuePair<List<string>, List<object[]>>(columns, values);
            }

            // Con las llaves foráneas ACTIVAS: se borra en orden inverso (hijas primero) y se inserta en orden directo (padres primero),
            // así MySQL valida además la integridad de la copia. Todos los comandos llevan la transacción de la conexión.
            var total = 0;
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    for (var index = BackupTables.Length - 1; index >= 0; index--)
                        using (var delete = new MySqlCommand("DELETE FROM `" + BackupTables[index] + "`", connection, transaction))
                            delete.ExecuteNonQuery();
                    foreach (var table in BackupTables)
                    {
                        var columns = prepared[table].Key;
                        var values = prepared[table].Value;
                        if (values.Count == 0) continue;
                        var sql = "INSERT INTO `" + table + "` (" + string.Join(", ", columns.Select(x => "`" + x + "`")) + ") VALUES (" +
                                  string.Join(", ", columns.Select((x, i) => "@p" + i)) + ")";
                        using (var insert = new MySqlCommand(sql, connection, transaction))
                        {
                            for (var index = 0; index < columns.Count; index++) insert.Parameters.Add(new MySqlParameter("@p" + index, null));
                            foreach (var row in values)
                            {
                                for (var index = 0; index < columns.Count; index++) insert.Parameters[index].Value = row[index] ?? DBNull.Value;
                                insert.ExecuteNonQuery();
                                total++;
                            }
                        }
                    }
                    RegistrarAuditoria(connection, transaction, AuditActions.CopiaRestaurada,
                        "Restauración de copia: " + total + " filas en " + BackupTables.Length + " tablas");
                    transaction.Commit();
                }
                catch (MySqlException ex) when ((int)ex.ErrorCode == 1451 || (int)ex.ErrorCode == 1452)
                {
                    try { transaction.Rollback(); } catch { }
                    LogError(ex, "Restaurar copia: llave foránea");
                    throw new InvalidDataException("La copia de seguridad tiene datos inconsistentes y no se restauró. No se modificó nada.", ex);
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
            return total;
        }

        // Nombre de columna -> tipo de MySQL, tomado del propio servidor (única fuente de nombres de columna válidos).
        private static Dictionary<string, string> ReadColumnTypes(MySqlConnection connection, string table)
        {
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var command = new MySqlCommand("SHOW COLUMNS FROM `" + table + "`", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var raw = reader.GetValue(1);
                    var type = raw is byte[] ? Encoding.UTF8.GetString((byte[])raw) : Convert.ToString(raw, CultureInfo.InvariantCulture);
                    types[reader.GetString(0)] = (type ?? "").ToLowerInvariant();
                }
            }
            return types;
        }

        private static object ConvertBackupValue(object value, string columnType, string table, string column)
        {
            if (value == null) return null;
            try
            {
                if (columnType.StartsWith("tinyint(1)", StringComparison.Ordinal))
                {
                    if (value is bool) return value;
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                }
                if (columnType.StartsWith("datetime", StringComparison.Ordinal) || columnType.StartsWith("timestamp", StringComparison.Ordinal))
                    return DateTime.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture),
                        new[] { BackupDateFormat, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (columnType.StartsWith("decimal", StringComparison.Ordinal))
                    return decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture);
                if (columnType.StartsWith("bigint", StringComparison.Ordinal) || columnType.StartsWith("int", StringComparison.Ordinal) ||
                    columnType.StartsWith("tinyint", StringComparison.Ordinal) || columnType.StartsWith("smallint", StringComparison.Ordinal) ||
                    columnType.StartsWith("mediumint", StringComparison.Ordinal))
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new InvalidDataException("La copia tiene un valor no válido en " + table + "." + column + ".", ex);
            }
        }

        // Los ALTER no son transaccionales: se ejecutan tras el commit. Un fallo aquí no invalida los datos ya restaurados.
        private static void AdjustBackupAutoIncrement(MySqlConnection connection)
        {
            var targets = new[]
            {
                new[] { "rol", "id_rol" }, new[] { "usuario", "id_usuario" },
                new[] { "movimiento_inventario", "secuencia" }, new[] { "auditoria", "id_auditoria" }
            };
            foreach (var target in targets)
            {
                try
                {
                    long next;
                    using (var command = new MySqlCommand("SELECT COALESCE(MAX(`" + target[1] + "`), 0) + 1 FROM `" + target[0] + "`", connection))
                        next = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    RunSql(connection, "ALTER TABLE `" + target[0] + "` AUTO_INCREMENT = " + next.ToString(CultureInfo.InvariantCulture));
                }
                catch (MySqlException ex)
                {
                    LogError(ex, "Ajustar AUTO_INCREMENT de " + target[0] + " tras restaurar la copia");
                }
            }
        }

        public static void LogError(Exception exception, string context)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var text = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + context + Environment.NewLine +
                           (exception == null ? "Error desconocido" : exception.ToString()) + Environment.NewLine + new string('-', 80) + Environment.NewLine;
                File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), text, Encoding.UTF8);
            }
            catch { }
        }

        private static string PathFor(string name) { return Path.Combine(DataDirectory, name + ".json"); }

        private static T Load<T>(string name, Func<T> fallback)
        {
            lock (Sync)
            {
                try
                {
                    var path = PathFor(name);
                    if (!File.Exists(path)) return fallback();
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    var result = Json.Deserialize<T>(text);
                    return result == null ? fallback() : result;
                }
                catch
                {
                    var path = PathFor(name);
                    if (File.Exists(path)) File.Copy(path, path + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                    return fallback();
                }
            }
        }

        private static void Save<T>(string name, T value)
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DataDirectory);
                var path = PathFor(name);
                var json = PrettyJson(Json.Serialize(value));
                AtomicFile.WriteAllText(path, json);
            }
        }

        private static void RaiseChanged()
        {
            var handler = DataChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }

        // Filas base sin las que la app no funciona: roles y la fila única de configuración (idempotente, no pisa lo existente).
        private static void EnsureBaseRows(MySqlConnection connection, MySqlTransaction transaction)
        {
            foreach (var role in new[] { UserRoles.Owner, UserRoles.Worker })
                Execute(connection, transaction, "INSERT INTO rol (nombre) VALUES (@nombre) ON DUPLICATE KEY UPDATE nombre = nombre",
                    new Dictionary<string, object> { { "@nombre", role } });

            var defaults = CreateDefaultSettings();
            Execute(connection, transaction,
                "INSERT INTO configuracion (id, store_name, employee_name, currency_symbol, next_receipt_number, next_shift_number, dni_lookup_enabled) " +
                "VALUES (1, @tienda, @empleado, @moneda, @recibo, @turno, @dni) ON DUPLICATE KEY UPDATE id = id",
                new Dictionary<string, object>
                {
                    { "@tienda", defaults.StoreName }, { "@empleado", defaults.EmployeeName }, { "@moneda", defaults.CurrencySymbol },
                    { "@recibo", defaults.NextReceiptNumber }, { "@turno", defaults.NextShiftNumber }, { "@dni", defaults.DniLookupEnabled }
                });
        }

        // Siembra inicial en MySQL (idempotente): roles de la app, fila única de configuración y, si no hay
        // ninguna dueña activa, la dueña por defecto (igual que hacía el arranque con JSON). No importa datos del JSON.
        private static bool MigrateDefaults()
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                EnsureBaseRows(connection, transaction);

                long activeOwners;
                using (var command = new MySqlCommand(
                    "SELECT COUNT(*) FROM usuario u INNER JOIN rol r ON r.id_rol = u.id_rol WHERE r.nombre = @rol AND u.activo = 1", connection, transaction))
                {
                    command.Parameters.AddWithValue("@rol", UserRoles.Owner);
                    activeOwners = Convert.ToInt64(command.ExecuteScalar());
                }
                if (activeOwners == 0) InsertUser(connection, transaction, UserSecurity.CreateUser("DUEÑA", "0000", UserRoles.Owner));
                transaction.Commit();
                return activeOwners == 0;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            if (!Directory.Exists(source)) return;
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = directory.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void EnsureDirectoryWritable(string directory)
        {
            var test = Path.Combine(directory, ".edenpos-write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllText(test, "ok"); File.Delete(test); }
            catch { throw new UnauthorizedAccessException("La carpeta del programa no permite escritura. Mueva Edén POS a Documentos o a una memoria USB antes de activar el modo portable."); }
        }

        private static void WriteZipText(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var output = new StreamWriter(entry.Open(), new UTF8Encoding(false))) output.Write(text);
        }

        private static string PrettyJson(string json)
        {
            var result = new StringBuilder();
            var quoted = false;
            var escaped = false;
            var indent = 0;
            foreach (var ch in json)
            {
                if (escaped) { result.Append(ch); escaped = false; continue; }
                if (ch == '\\' && quoted) { result.Append(ch); escaped = true; continue; }
                if (ch == '"') { quoted = !quoted; result.Append(ch); continue; }
                if (quoted) { result.Append(ch); continue; }
                switch (ch)
                {
                    case '{':
                    case '[': result.Append(ch).AppendLine(); indent++; result.Append(new string(' ', indent * 2)); break;
                    case '}':
                    case ']': result.AppendLine(); indent--; result.Append(new string(' ', indent * 2)).Append(ch); break;
                    case ',': result.Append(ch).AppendLine().Append(new string(' ', indent * 2)); break;
                    case ':': result.Append(": "); break;
                    default: if (!char.IsWhiteSpace(ch)) result.Append(ch); break;
                }
            }
            return result.ToString();
        }

        private static AppSettings CreateDefaultSettings()
        {
            return new AppSettings
            {
                StoreName = "BAÑOS SAUNA EDÉN",
                EmployeeName = "VICTOR",
                CurrencySymbol = "S/ ",
                NextReceiptNumber = 1,
                NextShiftNumber = 1,
                DniLookupEnabled = false,
                DniApiTokenProtected = "",
                MySqlConnectionStringProtected = "",
                InventorySchemaVersion = 2,
                CatalogSchemaVersion = 2,
                SecuritySchemaVersion = 1,
                Users = new List<AppUser>()
            };
        }

        private static List<Product> CreateDefaultProducts()
        {
            var products = new List<Product>();
            Action<string, string, decimal, string, string> add = (category, name, price, image, color) => products.Add(new Product
            {
                Id = Guid.NewGuid().ToString("N"), Category = category, Name = name, Price = price,
                ImageFile = image, ColorHex = color, Active = true,
                TrackInventory = false
            });

            add("ALQUILERES", "ALQ BIKINI MUJER", 5m, "swimsuit.png", "#1EA7E1");
            add("ALQUILERES", "ALQ FLOTADOR", 5m, "float.png", "#00A7C8");
            add("ALQUILERES", "ALQ GORRO", 3m, "cap.png", "#36A9E1");
            add("ALQUILERES", "ALQ LENTES", 4m, "goggles.png", "#3F51B5");
            add("ALQUILERES", "ALQ SANDALIA", 3m, "sandal.png", "#00A8D8");
            add("ALQUILERES", "ALQ SHORT", 5m, "shorts.png", "#29B6F6");
            add("ALQUILERES", "ALQ TOALLA", 3m, "towel.png", "#42A5F5");
            add("ALQUILERES", "PAREO", 4m, "pareo.png", "#039BE5");

            add("ASEO & VENTAS", "ACONDICIONADOR", 2m, "lotion.png", "#FFB74D");
            add("ASEO & VENTAS", "AFEITADOR", 2m, "razor.png", "#78909C");
            add("ASEO & VENTAS", "BICARBONATO", 2m, "salt.png", "#039BE5");
            add("ASEO & VENTAS", "BLOQUEADOR", 5m, "sun.png", "#FFB300");
            add("ASEO & VENTAS", "DESODORANTE", 4m, "spray.png", "#8E24AA");
            add("ASEO & VENTAS", "ESPONJA DE BAÑO", 3m, "sponge.png", "#F48FB1");
            add("ASEO & VENTAS", "EXFOLIANTE SAUNA", 5m, "towel.png", "#26C6DA");
            add("ASEO & VENTAS", "GANCHOS CABELLO", 2m, "hair.png", "#6D4C41");
            add("ASEO & VENTAS", "GORROS", 3m, "cap.png", "#E64A19");
            add("ASEO & VENTAS", "JABONES", 1m, "soap.png", "#EC407A");
            add("ASEO & VENTAS", "LENTES", 4m, "glasses.png", "#EF5350");
            add("ASEO & VENTAS", "MANO SAUNA", 5m, "glove.png", "#AB47BC");
            add("ASEO & VENTAS", "SHAMPOOS", 1m, "shampoo.png", "#42A5F5");

            add("CAFETERÍA", "GASEOSAS", 4m, "soda.png", "#6D4C41");
            add("CAFETERÍA", "HIDRATANTE", 1.50m, "water.png", "#29B6F6");
            add("CAFETERÍA", "JARRAS", 5m, "pitcher.png", "#FDD835");
            add("CAFETERÍA", "MARCIANOS", 2m, "alien.png", "#7E57C2");
            add("CAFETERÍA", "SANDWICHES", 2.50m, "sandwich.png", "#8D6E63");
            add("CAFETERÍA", "SNACKS", 1.50m, "cookie.png", "#795548");

            add("SAUNA", "ADULTO MAYOR", 15m, "elder.png", "#7CB342");
            add("SAUNA", "AMIGOS X3", 48m, "friends.png", "#43A047");
            add("SAUNA", "AMIGOS X4", 60m, "friends.png", "#43A047");
            add("SAUNA", "AMIGOS X5", 72m, "friends.png", "#43A047");
            add("SAUNA", "FAMILIA 1", 35m, "family.png", "#F39C12");
            add("SAUNA", "FAMILIA 2", 45m, "family.png", "#F39C12");
            add("SAUNA", "GENERAL", 18m, "sauna.png", "#7CB342");
            add("SAUNA", "MUJER 2", 30m, "woman.png", "#E91E63");
            add("SAUNA", "MUJER 3", 42m, "woman.png", "#E91E63");
            add("SAUNA", "MUJER 4", 52m, "woman.png", "#E91E63");
            add("SAUNA", "PAREJA", 32m, "couple.png", "#9C27B0");
            add("SAUNA", "NIÑO", 10m, "child.png", "#26A69A");
            add("SAUNA", "PASE CORTESÍA", 0m, "gift.png", "#FBC02D");
            add("SAUNA", "PRÉSTAMO GORRO", 3m, "cap.png", "#F4511E");
            add("SAUNA", "VIP", 20m, "crown.png", "#F9A825");
            foreach (var product in products.Where(x => ProductOptionCatalog.HasTemplate(x.Name)))
                product.Options = ProductOptionCatalog.CreateDefaultOptions(product.Name);
            return products;
        }
    }
}
