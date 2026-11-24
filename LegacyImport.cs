using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MySqlConnector;

namespace EdenPOS
{
    // Resultado de la importación de los JSON antiguos: filas por tipo y ajustes aplicados.
    public sealed class LegacyImportSummary
    {
        private readonly List<KeyValuePair<string, int>> counts = new List<KeyValuePair<string, int>>();
        private readonly List<string> adjustments = new List<string>();

        public IList<KeyValuePair<string, int>> Counts { get { return counts; } }
        public IList<string> Adjustments { get { return adjustments; } }
        public int TotalRows { get { return counts.Sum(x => x.Value); } }

        internal void AddCount(string label, int count) { counts.Add(new KeyValuePair<string, int>(label, count)); }
        internal void AddAdjustment(string text) { adjustments.Add(text); }

        public string ToText(int maxAdjustments = 12)
        {
            var text = new StringBuilder();
            text.AppendLine("Filas importadas:");
            foreach (var item in counts) text.AppendLine("  " + item.Key + ": " + item.Value.ToString(CultureInfo.CurrentCulture));
            if (adjustments.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Ajustes aplicados:");
                foreach (var item in adjustments.Take(maxAdjustments)) text.AppendLine("  - " + item);
                if (adjustments.Count > maxAdjustments) text.AppendLine("  - ... y " + (adjustments.Count - maxAdjustments) + " ajustes más.");
            }
            text.AppendLine();
            text.Append("Los archivos originales no se modificaron.");
            return text.ToString();
        }
    }

    public static partial class AppData
    {
        private static readonly string[] LegacyListFiles =
        {
            "catalog", "inventory_movements", "receipts", "saved_tickets", "customers", "shifts", "cash_movements"
        };

        // true si la base tiene datos de negocio reales (la importación única nunca mezcla ni duplica).
        // Una base que SOLO tiene la siembra por defecto (productos de ejemplo y, como mucho, la usuaria DUEÑA) cuenta como vacía.
        public static bool HasBusinessData()
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
                return HasBusinessData(connection, null);
        }

        private static bool HasBusinessData(MySqlConnection connection, MySqlTransaction transaction)
        {
            using (var command = new MySqlCommand(
                "SELECT (SELECT COUNT(*) FROM cliente) + (SELECT COUNT(*) FROM recibo) + (SELECT COUNT(*) FROM sesion_caja) + " +
                "(SELECT COUNT(*) FROM movimiento_caja) + (SELECT COUNT(*) FROM ticket_guardado) + (SELECT COUNT(*) FROM movimiento_inventario) + " +
                "(SELECT COUNT(*) FROM auditoria) + (SELECT COUNT(*) FROM usuario WHERE nombre <> @duena)", connection, transaction))
            {
                command.Parameters.AddWithValue("@duena", "DUEÑA");
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        // true si la base está vacía y DataDirectory tiene JSON antiguos con datos (no cuenta un settings.json solo con la cadena de conexión).
        public static bool HasLegacyJsonToImport()
        {
            lock (Sync)
            {
                if (!Directory.Exists(DataDirectory)) return false;
                if (HasBusinessData()) return false; // consulta barata primero; los JSON solo se leen con la base vacía
                return HasLegacyJsonFiles(DataDirectory);
            }
        }

        // true si la carpeta tiene algún JSON antiguo con contenido (lista no vacía, o usuarios en settings.json).
        public static bool HasLegacyJsonFiles(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            foreach (var name in LegacyListFiles)
            {
                var path = Path.Combine(directory, name + ".json");
                try
                {
                    if (!File.Exists(path)) continue;
                    var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (text.Length > 0 && text != "[]" && text != "null") return true;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            try
            {
                var settingsPath = Path.Combine(directory, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var data = Json.DeserializeObject(File.ReadAllText(settingsPath, Encoding.UTF8)) as Dictionary<string, object>;
                    object users;
                    var list = data != null && data.TryGetValue("Users", out users) ? users as System.Collections.IList : null;
                    if (list != null && list.Count > 0) return true;
                }
            }
            catch { }
            return false;
        }

        // Importación única de los JSON antiguos a MySQL en UNA transacción. Solo con la base vacía. Nunca toca los archivos.
        public static LegacyImportSummary ImportLegacyJson(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException("No se encontró la carpeta de datos antiguos.");
            lock (Sync)
                return new LegacyImporter(sourceDirectory, false).Run();
        }

        // Copia de seguridad de la versión anterior (zip con los JSON en la raíz): reemplaza los datos actuales en una transacción.
        private static int ImportLegacyBackup(Dictionary<string, byte[]> entries)
        {
            var temporary = Path.Combine(Path.GetTempPath(), "EdenPOS-legacy-" + Guid.NewGuid().ToString("N"));
            var restoredFiles = new List<KeyValuePair<string, byte[]>>();
            foreach (var item in entries)
            {
                if (!item.Key.StartsWith("Images/", StringComparison.OrdinalIgnoreCase)) continue;
                restoredFiles.Add(new KeyValuePair<string, byte[]>(ResolveInsideDataDirectory(item.Key), item.Value));
            }

            LegacyImporter importer;
            LegacyImportSummary summary;
            try
            {
                Directory.CreateDirectory(temporary);
                // Solo los nombres de archivo conocidos, en una carpeta propia: no hay forma de escribir fuera de ella.
                foreach (var name in LegacyListFiles.Concat(new[] { "settings" }))
                {
                    byte[] bytes;
                    if (entries.TryGetValue(name + ".json", out bytes))
                        File.WriteAllBytes(Path.Combine(temporary, name + ".json"), bytes);
                }
                lock (Sync)
                {
                    importer = new LegacyImporter(temporary, true);
                    summary = importer.Run();
                }
            }
            finally
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }

            // Desde aquí la base ya se reemplazó: los fallos se informan como "restaurado, pero...".
            var notes = new List<string>();
            var warnings = new List<string>();
            if (importer.DefaultOwnerCreated) notes.Add(DefaultOwnerNote);
            if (!string.IsNullOrEmpty(importer.PostCommitProblem)) warnings.Add(importer.PostCommitProblem);
            return FinishRestore(summary.TotalRows, restoredFiles, notes, warnings);
        }

        private sealed class LegacyImporter
        {
            private readonly string directory;
            private readonly bool replaceExisting;
            private readonly LegacyImportSummary summary = new LegacyImportSummary();
            private readonly Dictionary<string, int> clipped = new Dictionary<string, int>();
            private readonly DateTime importedAt = NowWithoutFraction();
            private int emptyDates;
            private int roundedAmounts;
            // Resultado de los pasos posteriores al commit (nunca lanzan: los datos ya están importados).
            public bool DefaultOwnerCreated { get; private set; }
            public string PostCommitProblem { get; private set; }

            public LegacyImporter(string sourceDirectory, bool replace)
            {
                directory = sourceDirectory;
                replaceExisting = replace;
            }

            public LegacyImportSummary Run()
            {
                // 1. Lectura y validación completa ANTES de abrir la transacción.
                var settings = ReadSettings();
                var users = ReadList<AppUser>("settings_users", settings.Users);
                var products = ReadList<Product>("catalog", null);
                var movements = ReadList<InventoryMovement>("inventory_movements", null);
                var receipts = ReadList<Receipt>("receipts", null);
                var tickets = ReadList<SavedTicket>("saved_tickets", null);
                var customers = ReadList<Customer>("customers", null);
                var shifts = ReadList<Shift>("shifts", null);
                var cashMovements = ReadList<CashMovement>("cash_movements", null);

                CheckIds(users.Select(x => x.Id), "usuarios", false);
                CheckIds(products.Select(x => x.Id), "catalog.json (artículos)", true);
                CheckIds(products.SelectMany(x => x.Options ?? new List<ProductOption>()).Select(x => x == null ? null : x.Id), "catalog.json (variantes)", true);
                CheckIds(movements.Select(x => x.Id), "inventory_movements.json", true);
                CheckIds(receipts.Select(x => x.Id), "receipts.json", true);
                CheckIds(tickets.Select(x => x.Id), "saved_tickets.json", true);
                CheckIds(shifts.Select(x => x.Id), "shifts.json", true);
                CheckIds(cashMovements.Select(x => x.Id), "cash_movements.json", true);

                ApplyCatalogMigrations(products, settings);

                var lookupEnabled = settings.DniLookupEnabled &&
                                    !string.IsNullOrWhiteSpace(UnprotectSecret(LoadLocalSettings().DniApiTokenProtected));
                if (settings.DniLookupEnabled && !lookupEnabled)
                    summary.AddAdjustment("La búsqueda de DNI quedó desactivada: no hay un token de DNI guardado en este equipo.");

                // 2. Una sola transacción.
                var problems = new List<string>();
                using (var connection = SqlConnectionFactory.Open())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            if (!replaceExisting && HasBusinessData(connection, transaction))
                                throw new InvalidOperationException("La base de datos ya tiene datos de negocio. La importación única solo se hace con la base vacía, para no mezclar ni duplicar información.");
                            // Con replaceExisting, o con una base que solo tiene la siembra por defecto, se reemplaza todo en esta misma transacción.
                            ClearBusinessTables(connection, transaction);
                            EnsureBaseRows(connection, transaction);

                            var userIds = InsertUsers(connection, transaction, users);
                            InsertCustomers(connection, transaction, customers);
                            var productIds = new HashSet<string>(products.Select(x => x.Id));
                            var variantOwners = products.SelectMany(p => (p.Options ?? new List<ProductOption>()).Select(o => new KeyValuePair<string, string>(o.Id, p.Id)))
                                .ToDictionary(x => x.Key, x => x.Value);
                            InsertProducts(connection, transaction, products);
                            InsertInventoryMovements(connection, transaction, movements, productIds, variantOwners);
                            InsertTickets(connection, transaction, tickets);
                            var shiftWindows = InsertShifts(connection, transaction, shifts, userIds);
                            InsertCashMovements(connection, transaction, cashMovements, shifts, userIds);
                            var maxReceiptNumber = InsertReceipts(connection, transaction, receipts, shiftWindows);

                            var maxShiftNumber = shifts.Count == 0 ? 0 : shifts.Max(x => x.Number);
                            var nextReceipt = Math.Max(Math.Max(1, settings.NextReceiptNumber), maxReceiptNumber + 1);
                            var nextShift = Math.Max(Math.Max(1, settings.NextShiftNumber), maxShiftNumber + 1);
                            Execute(connection, transaction,
                                "UPDATE configuracion SET store_name = @tienda, employee_name = @empleado, currency_symbol = @moneda, " +
                                "next_receipt_number = @recibo, next_shift_number = @turno, dni_lookup_enabled = @dni WHERE id = 1",
                                new Dictionary<string, object>
                                {
                                    { "@tienda", Fit(settings.StoreName, 80, "configuracion.store_name") },
                                    { "@empleado", Fit(settings.EmployeeName, 80, "configuracion.employee_name") },
                                    { "@moneda", Fit(settings.CurrencySymbol, 5, "configuracion.currency_symbol") },
                                    { "@recibo", nextReceipt }, { "@turno", nextShift }, { "@dni", lookupEnabled }
                                });
                            RegistrarAuditoria(connection, transaction, AuditActions.DatosImportados,
                                "Importación de datos antiguos: " + summary.TotalRows + " filas");
                            if (nextReceipt != settings.NextReceiptNumber)
                                summary.AddAdjustment("Siguiente número de recibo: " + nextReceipt + " (ajustes antiguos: " + settings.NextReceiptNumber + ", mayor recibo importado: " + maxReceiptNumber + ").");
                            if (nextShift != settings.NextShiftNumber)
                                summary.AddAdjustment("Siguiente número de turno: " + nextShift + " (ajustes antiguos: " + settings.NextShiftNumber + ", mayor turno importado: " + maxShiftNumber + ").");
                            transaction.Commit();
                        }
                        catch
                        {
                            try { transaction.Rollback(); } catch { }
                            throw;
                        }
                    }
                    // Los ALTER no son transaccionales: van tras el commit.
                    try { AdjustBackupAutoIncrement(connection); }
                    catch (Exception ex) { LogError(ex, "Ajustar contadores tras importar datos antiguos"); problems.Add("no se pudieron ajustar los contadores de identificadores (" + DescribeAnyError(ex) + ")"); }
                }

                // Ya hay commit: de aquí en adelante ningún fallo debe presentarse como si no se hubiera importado nada.
                if (!users.Any(x => x.Active && x.Role == UserRoles.Owner))
                    summary.AddAdjustment("Los datos antiguos no tienen una dueña activa: se creó el usuario DUEÑA predeterminado.");
                try { DefaultOwnerCreated = MigrateDefaults(); }
                catch (Exception ex) { LogError(ex, "Completar datos base tras importar datos antiguos"); problems.Add("no se pudieron completar los datos base (" + DescribeAnyError(ex) + ")"); }
                currencySymbolCache = null;

                foreach (var item in clipped.OrderBy(x => x.Key))
                {
                    var parts = item.Key.Split('|');
                    summary.AddAdjustment("Recortados " + item.Value + " valores de " + parts[0] + " al máximo de " + parts[1] + " caracteres.");
                }
                if (emptyDates > 0) summary.AddAdjustment(emptyDates + " fechas vacías se reemplazaron por la fecha de la importación.");
                if (roundedAmounts > 0) summary.AddAdjustment(roundedAmounts + " importes con más de 2 decimales se redondearon.");
                try { RaiseChanged(); }
                catch (Exception ex) { LogError(ex, "Actualizar pantallas tras importar datos antiguos"); problems.Add("no se pudieron actualizar las pantallas (" + DescribeAnyError(ex) + ")"); }
                if (problems.Count > 0)
                {
                    PostCommitProblem = "no se pudo terminar: " + string.Join("; ", problems);
                    summary.AddAdjustment("Los datos se importaron, pero " + string.Join("; ", problems) + ".");
                }
                return summary;
            }

            // ---- Lectura ----

            private AppSettings ReadSettings()
            {
                var path = Path.Combine(directory, "settings.json");
                if (!File.Exists(path)) return CreateDefaultSettings();
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return CreateDefaultSettings();
                try
                {
                    var data = Json.DeserializeObject(text) as Dictionary<string, object>;
                    // Un settings.json que solo trae la cadena de conexión o el token no es de la versión antigua: sin versiones que migrar.
                    if (data == null || !(data.ContainsKey("StoreName") || data.ContainsKey("Users"))) return CreateDefaultSettings();
                    var settings = Json.Deserialize<AppSettings>(text) ?? CreateDefaultSettings();
                    var defaults = CreateDefaultSettings();
                    if (settings.Users == null) settings.Users = new List<AppUser>();
                    if (string.IsNullOrWhiteSpace(settings.StoreName)) settings.StoreName = defaults.StoreName;
                    if (string.IsNullOrWhiteSpace(settings.EmployeeName)) settings.EmployeeName = defaults.EmployeeName;
                    if (string.IsNullOrEmpty(settings.CurrencySymbol)) settings.CurrencySymbol = defaults.CurrencySymbol;
                    return settings;
                }
                catch (Exception ex) when (!(ex is InvalidDataException))
                {
                    throw new InvalidDataException("El archivo settings.json no tiene un formato válido: " + ex.Message, ex);
                }
            }

            // fallback != null: lista ya cargada (los usuarios vienen dentro de settings.json).
            private List<T> ReadList<T>(string name, List<T> fallback) where T : class
            {
                if (fallback != null) return fallback.Where(x => x != null).ToList();
                var path = Path.Combine(directory, name + ".json");
                if (!File.Exists(path)) return new List<T>();
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return new List<T>();
                try
                {
                    var list = Json.Deserialize<List<T>>(text);
                    return list == null ? new List<T>() : list.Where(x => x != null).ToList();
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("El archivo " + name + ".json no tiene un formato válido: " + ex.Message, ex);
                }
            }

            private static void CheckIds(IEnumerable<string> ids, string source, bool mustFit)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Hay un registro sin identificador en " + source + ".");
                    if (mustFit && id.Length > 32) throw new InvalidDataException("Hay un identificador demasiado largo en " + source + ".");
                    if (!seen.Add(id)) throw new InvalidDataException("Hay identificadores repetidos en " + source + ".");
                }
            }

            // ---- Migraciones de catálogo antiguas (versiones de inventario/catálogo, precio de ADULTO MAYOR, imágenes, variantes) ----

            private void ApplyCatalogMigrations(List<Product> products, AppSettings settings)
            {
                int changed;
                if (settings.InventorySchemaVersion < 1)
                {
                    changed = 0;
                    foreach (var product in products)
                    {
                        var track = product.Category == "ASEO & VENTAS" || product.Category == "CAFETERÍA";
                        if (product.TrackInventory != track) changed++;
                        product.TrackInventory = track;
                    }
                    if (changed > 0) summary.AddAdjustment("Inventario v1: control de existencias recalculado por categoría en " + changed + " artículos.");
                }
                if (settings.InventorySchemaVersion < 2)
                {
                    changed = 0;
                    foreach (var product in products.Where(x => x.TrackInventory && x.InitialStock == 0 && x.Stock == 0))
                    {
                        product.TrackInventory = false;
                        changed++;
                    }
                    if (changed > 0) summary.AddAdjustment("Inventario v2: se desactivó el control de existencias en " + changed + " artículos sin saldo inicial.");
                }
                if (settings.CatalogSchemaVersion < 1)
                {
                    changed = 0;
                    foreach (var product in products.Where(ProductOptionCatalog.HasOptions))
                    {
                        var options = ProductOptionCatalog.GetOptions(product);
                        if (options.Count > 0) product.Price = options.Min(x => x.Price);
                        product.TrackInventory = false;
                        changed++;
                    }
                    if (changed > 0) summary.AddAdjustment("Catálogo v1: precio base y control de existencias ajustados en " + changed + " artículos con variantes.");
                }
                var missingOptions = products.Where(x => x.Options == null && ProductOptionCatalog.HasTemplate(x.Name)).ToList();
                foreach (var product in missingOptions)
                    product.Options = ProductOptionCatalog.CreateDefaultOptions(product.Name);
                if (missingOptions.Count > 0)
                    summary.AddAdjustment("Catálogo v2: se crearon las variantes de plantilla de " + missingOptions.Count + " artículos.");
                changed = 0;
                foreach (var product in products.Where(x => x.Name == "ADULTO MAYOR" && x.Price == 12m))
                {
                    product.Price = 15m;
                    changed++;
                }
                if (changed > 0) summary.AddAdjustment("El precio de ADULTO MAYOR pasó de 12 a 15.");

                changed = 0;
                var imageRoots = new[] { directory, DataDirectory }
                    .Select(x => Path.GetFullPath(Path.Combine(x, "Images")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var product in products.Where(x => IsRootedPath(x.ImageFile)))
                {
                    try
                    {
                        var imagePath = Path.GetFullPath(product.ImageFile);
                        if (imageRoots.Any(root => imagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                        {
                            product.ImageFile = "data:" + Path.GetFileName(imagePath);
                            changed++;
                        }
                    }
                    catch { }
                }
                if (changed > 0) summary.AddAdjustment("Rutas de imagen absolutas convertidas a relativas en " + changed + " artículos.");
            }

            // ---- Utilidades ----

            // Una ruta con caracteres no válidos hace lanzar a Path.IsPathRooted: se trata como ruta relativa.
            private static bool IsRootedPath(string value)
            {
                try { return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value); }
                catch (ArgumentException) { return false; }
            }

            // Recorta al ancho de la columna con Clip y lo cuenta para el resumen.
            private string Fit(string value, int max, string label)
            {
                value = value ?? "";
                if (value.Length <= max) return value;
                var key = label + "|" + max.ToString(CultureInfo.InvariantCulture);
                int count;
                clipped.TryGetValue(key, out count);
                clipped[key] = count + 1;
                return Clip(value, max);
            }

            private string FitOrNull(string value, int max, string label)
            {
                return string.IsNullOrEmpty(value) ? null : Fit(value, max, label);
            }

            // JavaScriptSerializer entrega las fechas como UTC: se pasan a hora local y se truncan al segundo.
            private DateTime LocalSecond(DateTime value)
            {
                if (value.Ticks == 0 || value.Year < 1000)
                {
                    emptyDates++;
                    return importedAt;
                }
                if (value.Kind == DateTimeKind.Utc) value = value.ToLocalTime();
                return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
            }

            private object LocalSecondOrNull(DateTime? value)
            {
                return value.HasValue ? (object)LocalSecond(value.Value) : null;
            }

            private decimal Money(decimal value)
            {
                var rounded = Math.Round(value, 2);
                if (rounded != value) roundedAmounts++;
                return rounded;
            }

            private static void ClearBusinessTables(MySqlConnection connection, MySqlTransaction transaction)
            {
                // Hijas primero; rol y configuracion se conservan (la importación los actualiza).
                foreach (var table in new[]
                {
                    "auditoria", "comprobante", "recibo_linea", "recibo", "movimiento_caja", "sesion_caja", "casillero_llave", "ticket_linea",
                    "ticket_guardado", "movimiento_inventario", "producto_variante", "producto", "cliente", "usuario"
                })
                    Execute(connection, transaction, "DELETE FROM `" + table + "`", new Dictionary<string, object>());
            }

            // ---- Inserción ----

            private Dictionary<string, int> InsertUsers(MySqlConnection connection, MySqlTransaction transaction, List<AppUser> users)
            {
                var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var invalidRoles = 0;
                foreach (var user in users)
                {
                    var role = user.Role;
                    if (!UserRoles.IsValid(role)) { role = UserRoles.Worker; invalidRoles++; }
                    var copy = new AppUser
                    {
                        Name = Fit(user.Name, 60, "usuario.nombre"),
                        Role = role,
                        PinHash = user.PinHash ?? "",
                        PinSalt = user.PinSalt ?? "",
                        Active = user.Active,
                        CreatedAt = LocalSecond(user.CreatedAt),
                        UpdatedAt = LocalSecond(user.UpdatedAt)
                    };
                    var id = InsertUser(connection, transaction, copy);
                    // Con nombres repetidos, el enlace de turnos y movimientos prefiere al usuario activo (y, si no, al primero).
                    if (!ids.ContainsKey(copy.Name) || (user.Active && !activeNames.Contains(copy.Name))) ids[copy.Name] = id;
                    if (user.Active) activeNames.Add(copy.Name);
                }
                if (users.Count > 0) summary.AddAdjustment("Usuarios: los ids antiguos (GUID) se reemplazaron por ids numéricos nuevos; se conservaron el hash y la sal del PIN.");
                if (invalidRoles > 0) summary.AddAdjustment(invalidRoles + " usuarios con rol desconocido se importaron como Trabajador.");
                summary.AddCount("Usuarios", users.Count);
                return ids;
            }

            private object FindUserId(Dictionary<string, int> userIds, string employee)
            {
                int id;
                return userIds.TryGetValue(employee ?? "", out id) ? (object)id : null;
            }

            private void InsertCustomers(MySqlConnection connection, MySqlTransaction transaction, List<Customer> customers)
            {
                var byDni = new Dictionary<string, Customer>();
                var invalid = 0;
                var repeated = 0;
                var withoutConsent = 0;
                foreach (var customer in customers)
                {
                    var dni = Validation.NormalizeDni(customer.Dni);
                    if (dni.Length != 8) { invalid++; continue; }
                    if (byDni.ContainsKey(dni)) repeated++;
                    byDni[dni] = customer;
                }
                // RNF-09 / RN-14: el DNI (clave de cliente) solo se guarda con consentimiento. Los JSON antiguos no lo registraban
                // (DataConsent = false), así que esos clientes NO se importan; tampoco los que lo retiraron. Sus recibos sí se importan
                // con el DNI del comprobante (respaldo de la venta).
                foreach (var dni in byDni.Where(p => !p.Value.DataConsent || p.Value.ConsentRevokedAt.HasValue).Select(p => p.Key).ToList())
                {
                    byDni.Remove(dni);
                    withoutConsent++;
                }
                foreach (var pair in byDni)
                {
                    var customer = pair.Value;
                    var phone = Validation.NormalizePhone(customer.Phone ?? "");
                    Execute(connection, transaction,
                        "INSERT INTO cliente (dni, nombre_completo, origen, telefono, consentimiento_datos, consentimiento_fecha, created_at, updated_at) " +
                        "VALUES (@dni, @nombre, @origen, @telefono, 1, @consentimiento, @creado, @actualizado)",
                        new Dictionary<string, object>
                        {
                            { "@dni", pair.Key },
                            { "@nombre", Fit(customer.FullName, 150, "cliente.nombre_completo") },
                            { "@origen", Fit(customer.Source, 60, "cliente.origen") },
                            { "@telefono", phone.Length > 0 && Validation.IsValidPhone(phone) ? phone : null },
                            { "@consentimiento", LocalSecond(customer.ConsentDate ?? customer.UpdatedAt) },
                            { "@creado", LocalSecond(customer.CreatedAt) },
                            { "@actualizado", LocalSecond(customer.UpdatedAt) }
                        });
                }
                if (invalid > 0) summary.AddAdjustment(invalid + " clientes con DNI que no tiene 8 dígitos no se importaron.");
                if (repeated > 0) summary.AddAdjustment(repeated + " clientes con DNI repetido: se conservó la última fila de cada DNI.");
                if (withoutConsent > 0)
                    summary.AddAdjustment(withoutConsent + " clientes sin consentimiento registrado no se importaron (Ley N.º 29733): su DNI solo se guarda si el cliente lo autoriza.");
                summary.AddCount("Clientes", byDni.Count);
            }

            private void InsertProducts(MySqlConnection connection, MySqlTransaction transaction, List<Product> products)
            {
                var variantCount = 0;
                foreach (var product in products)
                {
                    var baseName = Fit(product.Name, 60, "producto.nombre");
                    var category = Fit(product.Category, 40, "producto.categoria");
                    var color = string.IsNullOrWhiteSpace(product.ColorHex) || product.ColorHex.Length > 7 ? "#808080" : product.ColorHex;
                    var attempt = 1;
                    while (true)
                    {
                        var suffix = " (" + attempt.ToString(CultureInfo.InvariantCulture) + ")";
                        var candidate = attempt == 1 ? baseName : Clip(baseName, 60 - suffix.Length) + suffix;
                        try
                        {
                            Execute(connection, transaction,
                                "INSERT INTO producto (id_producto, categoria, nombre, precio, imagen, color_hex, activo, controla_inventario, stock, stock_inicial, tipo) " +
                                "VALUES (@id, @categoria, @nombre, @precio, @imagen, @color, @activo, @controla, @stock, @inicial, @tipo)",
                                new Dictionary<string, object>
                                {
                                    { "@id", product.Id }, { "@categoria", category }, { "@nombre", candidate },
                                    { "@precio", Money(product.Price) },
                                    { "@imagen", FitOrNull(product.ImageFile, 255, "producto.imagen") },
                                    { "@color", color }, { "@activo", product.Active }, { "@controla", product.TrackInventory },
                                    { "@stock", product.Stock }, { "@inicial", product.InitialStock },
                                    // RF-14: los JSON antiguos no traen tipo ni cargo: ALQUILERES se importa como Implemento (misma regla que la migración 009).
                                    { "@tipo", ProductTypes.Normalize(product.Tipo, category, 0m) }
                                });
                            if (attempt > 1)
                                summary.AddAdjustment("Artículo repetido (misma categoría y nombre, sin distinguir mayúsculas ni tildes): \"" + baseName + "\" de " + category + " se renombró a \"" + candidate + "\".");
                            break;
                        }
                        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry &&
                                                        ex.Message.IndexOf("uq_producto_nombre_categoria", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            attempt++;
                            if (attempt > 1000) throw;
                        }
                    }

                    var order = 0;
                    foreach (var option in product.Options ?? new List<ProductOption>())
                    {
                        Execute(connection, transaction,
                            "INSERT INTO producto_variante (id_variante, id_producto, nombre, orden, precio, controla_inventario, stock, stock_inicial) " +
                            "VALUES (@id, @producto, @nombre, @orden, @precio, @controla, @stock, @inicial)",
                            new Dictionary<string, object>
                            {
                                { "@id", option.Id }, { "@producto", product.Id },
                                { "@nombre", Fit(option.Name, 80, "producto_variante.nombre") }, { "@orden", order++ },
                                { "@precio", Money(option.Price) }, { "@controla", option.TrackInventory },
                                { "@stock", option.Stock }, { "@inicial", option.InitialStock }
                            });
                        variantCount++;
                    }
                }
                summary.AddCount("Artículos", products.Count);
                summary.AddCount("Variantes de artículos", variantCount);
            }

            private void InsertInventoryMovements(MySqlConnection connection, MySqlTransaction transaction, List<InventoryMovement> movements,
                HashSet<string> productIds, Dictionary<string, string> variantOwners)
            {
                var orphans = 0;
                foreach (var movement in movements)
                {
                    // En el JSON, ProductId es el id del artículo o el de la variante; en SQL van en columnas separadas.
                    string productId = null, variantId = null, owner;
                    var reference = movement.ProductId;
                    if (!string.IsNullOrEmpty(reference) && variantOwners.TryGetValue(reference, out owner)) { variantId = reference; productId = owner; }
                    else if (!string.IsNullOrEmpty(reference))
                    {
                        productId = Clip(reference, 32);
                        if (!productIds.Contains(reference)) orphans++;
                    }
                    Execute(connection, transaction,
                        "INSERT INTO movimiento_inventario (id_movimiento, id_producto, id_variante, producto_nombre, tipo, cantidad, stock_previo, stock_nuevo, nota, referencia_id, created_at) " +
                        "VALUES (@id, @producto, @variante, @nombre, @tipo, @cantidad, @previo, @nuevo, @nota, @referencia, @fecha)",
                        new Dictionary<string, object>
                        {
                            { "@id", movement.Id }, { "@producto", productId }, { "@variante", variantId },
                            { "@nombre", Fit(movement.ProductName, 120, "movimiento_inventario.producto_nombre") },
                            { "@tipo", Fit(movement.Type, 20, "movimiento_inventario.tipo") },
                            { "@cantidad", movement.Quantity }, { "@previo", movement.PreviousStock }, { "@nuevo", movement.NewStock },
                            { "@nota", Fit(movement.Note, 150, "movimiento_inventario.nota") },
                            { "@referencia", FitOrNull(movement.ReferenceId, 32, "movimiento_inventario.referencia_id") },
                            { "@fecha", LocalSecond(movement.CreatedAt) }
                        });
                }
                if (orphans > 0)
                    summary.AddAdjustment(orphans + " movimientos de inventario son de artículos que ya no existen: se conservó su id sin enlace.");
                summary.AddCount("Movimientos de inventario", movements.Count);
            }

            private void InsertTickets(MySqlConnection connection, MySqlTransaction transaction, List<SavedTicket> tickets)
            {
                var lineCount = 0;
                var lockerCount = 0;
                var lockerUse = new Dictionary<int, int>();
                foreach (var ticket in tickets)
                {
                    var ticketDni = Validation.NormalizeDni(ticket.CustomerDni);
                    Execute(connection, transaction,
                        "INSERT INTO ticket_guardado (id_ticket, nombre, cliente_nombre, cliente_dni, adultos, ninos, created_at, updated_at) " +
                        "VALUES (@id, @nombre, @cliente, @dni, @adultos, @ninos, @creado, @actualizado)",
                        new Dictionary<string, object>
                        {
                            { "@id", ticket.Id },
                            { "@nombre", Fit(ticket.Name, 80, "ticket_guardado.nombre") },
                            { "@cliente", Fit(ticket.CustomerName, 150, "ticket_guardado.cliente_nombre") },
                            { "@dni", ticketDni.Length == 8 ? ticketDni : null },
                            { "@adultos", ticket.AdultCount }, { "@ninos", ticket.ChildCount },
                            { "@creado", LocalSecond(ticket.CreatedAt) }, { "@actualizado", LocalSecond(ticket.UpdatedAt) }
                        });
                    var order = 0;
                    foreach (var line in ticket.Lines ?? new List<ReceiptLine>())
                    {
                        if (line == null) continue;
                        InsertLine(connection, transaction, "ticket_linea", "id_ticket", ticket.Id, order++, line);
                        lineCount++;
                    }
                    foreach (var locker in GetTicketLockers(ticket))
                    {
                        Execute(connection, transaction, "INSERT INTO casillero_llave (id_ticket, numero) VALUES (@id, @numero)",
                            new Dictionary<string, object> { { "@id", ticket.Id }, { "@numero", locker } });
                        int uses;
                        lockerUse.TryGetValue(locker, out uses);
                        lockerUse[locker] = uses + 1;
                        lockerCount++;
                    }
                }
                var repeated = lockerUse.Where(x => x.Value > 1).Select(x => x.Key).OrderBy(x => x).ToList();
                if (repeated.Count > 0)
                    summary.AddAdjustment("Llaves repetidas entre tickets guardados (se conservaron tal cual): " + string.Join(", ", repeated) + ".");
                summary.AddCount("Tickets guardados", tickets.Count);
                summary.AddCount("Líneas de tickets guardados", lineCount);
                summary.AddCount("Llaves de casillero", lockerCount);
            }

            private void InsertLine(MySqlConnection connection, MySqlTransaction transaction, string table, string ownerColumn, string ownerId, int order, ReceiptLine line)
            {
                Execute(connection, transaction,
                    "INSERT INTO " + table + " (" + ownerColumn + ", orden, id_producto, id_variante, nombre, comentario, precio_unitario, cantidad) " +
                    "VALUES (@id, @orden, @producto, @variante, @nombre, @comentario, @precio, @cantidad)",
                    new Dictionary<string, object>
                    {
                        { "@id", ownerId }, { "@orden", order },
                        { "@producto", FitOrNull(line.ProductId, 32, table + ".id_producto") },
                        { "@variante", FitOrNull(line.OptionId, 32, table + ".id_variante") },
                        { "@nombre", Fit(line.Name, 120, table + ".nombre") },
                        { "@comentario", FitOrNull(line.Comment, 150, table + ".comentario") },
                        { "@precio", Money(line.UnitPrice) }, { "@cantidad", line.Quantity }
                    });
            }

            // Devuelve los turnos con su ventana horaria local para asignar recibos.
            private List<ShiftWindow> InsertShifts(MySqlConnection connection, MySqlTransaction transaction, List<Shift> shifts, Dictionary<string, int> userIds)
            {
                var windows = new List<ShiftWindow>();
                var linked = 0;
                foreach (var shift in shifts)
                {
                    var employee = Fit(shift.Employee, 60, "sesion_caja.empleado");
                    var userId = FindUserId(userIds, employee);
                    if (userId != null) linked++;
                    var opened = LocalSecond(shift.OpenedAt);
                    DateTime? closed = shift.ClosedAt.HasValue ? LocalSecond(shift.ClosedAt.Value) : (DateTime?)null;
                    Execute(connection, transaction,
                        "INSERT INTO sesion_caja (id_sesion, numero, id_usuario, empleado, fecha_apertura, fecha_cierre, monto_inicial, monto_cierre, monto_esperado, abierta) " +
                        "VALUES (@id, @numero, @usuario, @empleado, @apertura, @cierre, @inicial, @montoCierre, @esperado, @abierta)",
                        new Dictionary<string, object>
                        {
                            { "@id", shift.Id }, { "@numero", shift.Number }, { "@usuario", userId }, { "@empleado", employee },
                            { "@apertura", opened }, { "@cierre", closed.HasValue ? (object)closed.Value : null },
                            { "@inicial", Money(shift.OpeningAmount) },
                            { "@montoCierre", shift.IsOpen ? null : (object)Money(shift.ClosingAmount) },
                            { "@esperado", shift.IsOpen ? null : (object)Money(shift.ExpectedAmount) },
                            { "@abierta", shift.IsOpen }
                        });
                    windows.Add(new ShiftWindow { Id = shift.Id, Opened = opened, Closed = closed });
                }
                var openShifts = shifts.Count(x => x.IsOpen);
                if (openShifts > 1) summary.AddAdjustment("Hay " + openShifts + " turnos abiertos en los datos antiguos; se importaron tal cual.");
                if (shifts.Count > linked) summary.AddAdjustment((shifts.Count - linked) + " turnos quedaron sin usuario enlazado (solo el nombre del empleado): no hay un usuario con ese nombre.");
                summary.AddCount("Turnos de caja", shifts.Count);
                return windows;
            }

            private void InsertCashMovements(MySqlConnection connection, MySqlTransaction transaction, List<CashMovement> movements, List<Shift> shifts, Dictionary<string, int> userIds)
            {
                var shiftIds = new HashSet<string>(shifts.Select(x => x.Id));
                var imported = 0;
                var orphans = 0;
                var unlinked = 0;
                foreach (var movement in movements)
                {
                    if (!shiftIds.Contains(movement.ShiftId ?? ""))
                    {
                        orphans++;
                        continue;
                    }
                    var employee = Fit(movement.Employee, 60, "movimiento_caja.empleado");
                    var userId = FindUserId(userIds, employee);
                    if (userId == null) unlinked++;
                    Execute(connection, transaction,
                        "INSERT INTO movimiento_caja (id_movimiento, id_sesion, id_usuario, empleado, tipo, importe, comentario, created_at) " +
                        "VALUES (@id, @sesion, @usuario, @empleado, @tipo, @importe, @comentario, @fecha)",
                        new Dictionary<string, object>
                        {
                            { "@id", movement.Id }, { "@sesion", movement.ShiftId }, { "@usuario", userId }, { "@empleado", employee },
                            { "@tipo", Fit(movement.Type, 10, "movimiento_caja.tipo") }, { "@importe", Money(movement.Amount) },
                            { "@comentario", Fit(movement.Comment, 150, "movimiento_caja.comentario") },
                            { "@fecha", LocalSecond(movement.CreatedAt) }
                        });
                    imported++;
                }
                if (orphans > 0) summary.AddAdjustment(orphans + " movimientos de caja de un turno que no existe no se importaron.");
                if (unlinked > 0) summary.AddAdjustment(unlinked + " movimientos de caja quedaron sin usuario enlazado (solo el nombre del empleado).");
                summary.AddCount("Movimientos de caja", imported);
            }

            // Devuelve el mayor número de recibo importado.
            private int InsertReceipts(MySqlConnection connection, MySqlTransaction transaction, List<Receipt> receipts, List<ShiftWindow> windows)
            {
                var lineCount = 0;
                var withoutShift = 0;
                var maxNumber = 0;
                foreach (var receipt in receipts)
                {
                    var created = LocalSecond(receipt.CreatedAt);
                    // Turno cuyo rango [apertura, cierre o sin tope] contiene la fecha; si hay solapes, el abierto más reciente.
                    var shift = windows.Where(x => x.Opened <= created && (!x.Closed.HasValue || created <= x.Closed.Value))
                        .OrderByDescending(x => x.Opened).FirstOrDefault();
                    if (shift == null) withoutShift++;
                    var dni = Validation.NormalizeDni(receipt.CustomerDni);
                    maxNumber = Math.Max(maxNumber, receipt.Number);
                    Execute(connection, transaction,
                        "INSERT INTO recibo (id_recibo, numero, id_sesion, created_at, empleado, cliente_nombre, cliente_dni, medio_pago, monto_recibido, monto_efectivo, monto_yape, total, estado, refunded_at, refund_reason) " +
                        "VALUES (@id, @numero, @sesion, @fecha, @empleado, @cliente, @dni, @medio, @recibido, @efectivo, @yape, @total, @estado, @reembolso, @motivo)",
                        new Dictionary<string, object>
                        {
                            { "@id", receipt.Id }, { "@numero", receipt.Number }, { "@sesion", shift == null ? null : shift.Id },
                            { "@fecha", created }, { "@empleado", Fit(receipt.Employee, 60, "recibo.empleado") },
                            { "@cliente", Fit(string.IsNullOrWhiteSpace(receipt.CustomerName) ? "Público general" : receipt.CustomerName, 150, "recibo.cliente_nombre") },
                            { "@dni", dni.Length == 0 ? null : dni },
                            { "@medio", Fit(receipt.PaymentMethod, 10, "recibo.medio_pago") },
                            { "@recibido", Money(receipt.AmountReceived) }, { "@efectivo", Money(receipt.CashAmount) },
                            { "@yape", Money(receipt.YapeAmount) }, { "@total", Money(receipt.Total) },
                            { "@estado", Fit(receipt.Status, 12, "recibo.estado") },
                            { "@reembolso", LocalSecondOrNull(receipt.RefundedAt) },
                            { "@motivo", FitOrNull(receipt.RefundReason, 200, "recibo.refund_reason") }
                        });
                    var order = 0;
                    foreach (var line in receipt.Lines ?? new List<ReceiptLine>())
                    {
                        if (line == null) continue;
                        InsertLine(connection, transaction, "recibo_linea", "id_recibo", receipt.Id, order++, line);
                        lineCount++;
                    }
                }
                if (withoutShift > 0)
                    summary.AddAdjustment(withoutShift + " recibos no caen dentro de ningún turno: quedaron sin turno asignado.");
                summary.AddCount("Recibos", receipts.Count);
                summary.AddCount("Líneas de recibos", lineCount);
                return maxNumber;
            }

            private sealed class ShiftWindow
            {
                public string Id;
                public DateTime Opened;
                public DateTime? Closed;
            }
        }
    }
}
