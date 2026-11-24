using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using EdenPOS;
using MySqlConnector;

internal static class RegressionChecks
{
    private static int checks;

    private static int Main()
    {
        var originalPath = Environment.GetEnvironmentVariable("EDENPOS_DATA_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "EdenPOS-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("EDENPOS_DATA_PATH", directory);
        try
        {
            CheckAtomicWrites(directory);
            CheckLocalSettings(directory);
            CheckErrorLog(directory);
            CheckDniResponses();
            CheckPinSecurity();
            CheckConnectionSettings(directory);
            CheckInventoryDatabase();
            CheckCustomerConsentDatabase();
            CheckPendingBalanceAndVisitsDatabase();
            Console.WriteLine("Correcto: " + checks + " comprobaciones.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDENPOS_DATA_PATH", originalPath);
            Directory.Delete(directory, true);
        }
    }

    private static object Invoke(Type type, object target, string method, params object[] arguments)
    {
        try
        {
            return type.GetMethod(method, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static void CheckAtomicWrites(string directory)
    {
        var atomicFile = typeof(AppData).Assembly.GetType("EdenPOS.AtomicFile", true);
        var path = Path.Combine(directory, "atomic.json");
        Action<string> write = text => Invoke(atomicFile, null, "WriteAllText", path, text);
        write("{\"nombre\":\"Edén\"}");
        Check(File.ReadAllText(path) == "{\"nombre\":\"Edén\"}", "Debe conservar el texto UTF-8.");
        Check(File.ReadAllBytes(path)[0] == (byte)'{', "No debe agregar BOM.");
        write("{\"version\":2}");
        Check(File.ReadAllText(path) == "{\"version\":2}", "Debe reemplazar un archivo existente.");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = false;
            try { write("incompleto"); } catch (IOException) { failed = true; }
            Check(failed, "Debe informar el fallo al reemplazar un archivo bloqueado.");
            using (var reader = new StreamReader(held, Encoding.UTF8, true, 1024, true))
                Check(reader.ReadToEnd() == "{\"version\":2}", "Un fallo no debe modificar el archivo anterior.");
        }
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Debe limpiar sus temporales incluso si falla.");
    }

    private static void CheckLocalSettings(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"OtroAjuste\":\"conservar\",\"DniApiTokenProtected\":\"token-anterior\"}");
        Invoke(typeof(AppData), null, "SaveLocalValues", "conexion-prueba", null);
        AppData.SaveAutoBackupConfig(false, "", 17);
        var settings = (Dictionary<string, object>)Invoke(typeof(AppData), null, "ReadLocalSettingsStrict");
        Check((string)settings["OtroAjuste"] == "conservar", "Debe conservar claves desconocidas.");
        Check((string)settings["DniApiTokenProtected"] == "token-anterior", "Una copia no debe sobrescribir el token.");
        Check((string)settings["MySqlConnectionStringProtected"] == "conexion-prueba", "Una copia no debe sobrescribir la conexión.");
        Check(Convert.ToInt32(settings["AutoBackupKeep"]) == 17, "Debe guardar la retención configurada.");
        const string invalid = "{archivo dañado";
        File.WriteAllText(path, invalid);
        var failed = false;
        try { AppData.SaveAutoBackupConfig(false, "", 30); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && File.ReadAllText(path) == invalid, "No debe sobrescribir ajustes que no se pueden interpretar.");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "El guardado de ajustes no debe dejar temporales.");
    }

    private static void CheckErrorLog(string directory)
    {
        var path = Path.Combine(directory, "errors.log");
        var previous = path + ".1";
        File.Delete(path);
        Parallel.For(0, 64, index => AppData.LogError(new InvalidOperationException("Fallo de prueba"), "Evento-" + index + ":"));
        var text = File.ReadAllText(path);
        for (var index = 0; index < 64; index++)
            Check(text.Contains("Evento-" + index + ":"), "No debe perder errores concurrentes: " + index);
        var large = new string('a', 2 * 1024 * 1024);
        File.WriteAllText(path, large);
        AppData.LogError(null, "Después de rotar");
        Check(File.ReadAllText(previous) == large, "Debe conservar el registro anterior al rotar.");
        Check(File.ReadAllText(path).Contains("Después de rotar"), "Debe escribir en un registro nuevo tras rotar.");
        Check(new FileInfo(path).Length < 4096, "El registro activo debe volver a ser pequeño.");
        File.WriteAllText(path, large + "segunda rotación");
        AppData.LogError(null, "Segunda rotación");
        Check(File.ReadAllText(previous) == large + "segunda rotación", "Debe reemplazar el archivo anterior al volver a rotar.");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            AppData.LogError(null, "Archivo ocupado");
        AppData.LogError(null, "Recuperado");
        Check(File.ReadAllText(path).Contains("Recuperado"), "Un archivo ocupado no debe impedir registrar errores después.");
    }

    private static void CheckDniResponses()
    {
        const string direct = "{\"success\":true,\"data\":{\"nombre_completo\":\"  José   Pérez  \"}}";
        var result = DniLookupService.ParseResponse(direct, "12345678");
        Check(result.Success && result.FullName == "JOSÉ PÉREZ", "Debe conservar nombres válidos y normalizar espacios.");
        result = DniLookupService.ParseResponse("{\"success\":true,\"data\":{\"nombres\":\"Ana\",\"apellido_paterno\":\"Pérez\",\"apellido_materno\":\"Díaz\"}}", "12345678");
        Check(result.Success && result.FullName == "PÉREZ DÍAZ ANA", "Debe conservar el formato de nombres separados.");
        foreach (var invalidName in new[] { "true", "12345", "{}", "[]", "null" })
            Check(!DniLookupService.ParseResponse("{\"success\":true,\"data\":{\"nombre_completo\":" + invalidName + "}}", "12345678").Success,
                "No debe interpretar tipos JSON como nombres: " + invalidName);
        Check(!DniLookupService.ParseResponse("{incompleto", "12345678").Success, "Debe tolerar respuestas dañadas.");
        Check(!DniLookupService.ParseResponse("{\"success\":false,\"message\":\"No encontrado\"}", "12345678").Success, "Debe conservar los fallos del proveedor.");
        Check(!DniLookupService.LookupAsync("12").GetAwaiter().GetResult().Success, "Debe rechazar DNI incompleto sin consultar la base.");
        Parallel.For(0, 64, index =>
        {
            var parsed = DniLookupService.ParseResponse(direct, "12345678");
            if (!parsed.Success || parsed.FullName != "JOSÉ PÉREZ")
                throw new InvalidOperationException("Falló el análisis concurrente de respuestas DNI.");
        });
        Check(true, "Debe analizar respuestas concurrentes sin mezclar resultados.");
    }

    private static void CheckPinSecurity()
    {
        var user = UserSecurity.CreateUser("Prueba", "1234", UserRoles.Worker);
        Check(UserSecurity.VerifyPin(user, "1234"), "Debe aceptar el PIN correcto.");
        Check(!UserSecurity.VerifyPin(user, "9876"), "Debe rechazar otro PIN.");
        var hash = user.PinHash;
        var salt = user.PinSalt;
        user.PinHash = Convert.ToBase64String(Convert.FromBase64String(hash).Take(1).ToArray());
        Check(!UserSecurity.VerifyPin(user, "1234"), "Un hash truncado no debe reducir la seguridad del PIN.");
        foreach (var invalid in new[] { " ", "\t", "AA==", Convert.ToBase64String(new byte[33]), "base64 inválido" })
        {
            user.PinHash = invalid;
            Check(!UserSecurity.VerifyPin(user, "1234"), "Debe rechazar hashes dañados sin autenticar.");
        }
        user.PinHash = hash;
        foreach (var invalid in new[] { "AA==", Convert.ToBase64String(new byte[17]), "base64 inválido" })
        {
            user.PinSalt = invalid;
            Check(!UserSecurity.VerifyPin(user, "1234"), "Debe rechazar sales dañadas.");
        }
        user.PinSalt = salt;
        user.Active = false;
        Check(!UserSecurity.VerifyPin(user, "1234"), "Un usuario inactivo no puede ingresar.");
    }

    private static void CheckConnectionSettings(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        var previous = File.ReadAllText(path);
        var environment = Environment.GetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION");
        try
        {
            Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", null);
            foreach (var invalid in new[] { "{dañado", "null", "{\"MySqlConnectionStringProtected\":123}", "{\"MySqlConnectionStringProtected\":\"cifrado-inválido\"}",
                "{\"MySqlConnectionStringProtected\":\"" + Convert.ToBase64String(new byte[64]) + "\"}" })
            {
                File.WriteAllText(path, invalid);
                SqlConnectionFactory.InvalidateCache();
                var failed = false;
                try { SqlConnectionFactory.ResolveConnectionString(); }
                catch (InvalidOperationException) { failed = true; }
                Check(failed, "No debe usar root como alternativa ante ajustes dañados.");
                Check(File.ReadAllText(path) == invalid, "Debe conservar la configuración dañada para recuperarla.");
            }
            Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION",
                "Server=127.0.0.1;Database=prueba;ConnectionReset=false;IgnoreCommandTransaction=true;PersistSecurityInfo=true;AllowLoadLocalInfile=true");
            var builder = new MySqlConnectionStringBuilder(SqlConnectionFactory.ResolveConnectionString());
            Check(builder.ConnectionReset && !builder.IgnoreCommandTransaction && !builder.PersistSecurityInfo && !builder.AllowLoadLocalInfile,
                "Debe aplicar las protecciones incluso si la cadena intenta desactivarlas.");
            Check(builder.Database == "prueba", "Debe conservar el destino configurado por el entorno.");
            Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", null);
            File.WriteAllText(path, "{}");
            SqlConnectionFactory.InvalidateCache();
            Check(new MySqlConnectionStringBuilder(SqlConnectionFactory.ResolveConnectionString()).Database == "edenpos",
                "El primer inicio sin conexión configurada conserva la compatibilidad local.");
        }
        finally
        {
            File.WriteAllText(path, previous);
            Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", environment);
            SqlConnectionFactory.InvalidateCache();
        }
    }

    private static void CheckDatabaseSession()
    {
        using (var connection = SqlConnectionFactory.Open())
        {
            Execute(connection, "SET SESSION foreign_key_checks = 0; SET SESSION sql_mode = ''; SET @edenpos_test_marker = 123");
        }
        using (var connection = SqlConnectionFactory.Open())
        {
            using (var command = new MySqlCommand("SELECT @@foreign_key_checks, @@sql_mode, @edenpos_test_marker", connection))
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                Check(reader.GetInt32(0) == 1, "Debe recuperar las claves foráneas al reutilizar una conexión.");
                Check(reader.GetString(1).Contains("STRICT_TRANS_TABLES"), "Debe mantener el modo estricto.");
                Check(reader.IsDBNull(2), "Las variables de sesión no deben filtrarse entre conexiones.");
            }
            using (var transaction = connection.BeginTransaction())
            {
                var rejected = false;
                try { Execute(connection, "SELECT 1"); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Debe rechazar comandos que omiten la transacción activa.");
            }
            using (var transaction = connection.BeginTransaction())
            using (var command = new MySqlCommand("INSERT INTO cliente (dni, nombre_completo, origen, created_at, updated_at) VALUES ('99999999', 'Temporal', 'Prueba', NOW(), NOW())", connection, transaction))
                command.ExecuteNonQuery();
            using (var command = new MySqlCommand("SELECT COUNT(*) FROM cliente WHERE dni = '99999999'", connection))
                Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "Una transacción no confirmada debe revertir sus cambios.");
        }
    }

    private static void CheckInventoryDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDENPOS_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SQL omitido: configure EDENPOS_TEST_MYSQL_CONNECTION para incluir la prueba de inventario.");
            return;
        }
        var builder = new MySqlConnectionStringBuilder(connectionString);
        // Siempre crea una base propia: no carga fixtures en la base indicada por el usuario.
        var database = "edenpos_test_" + Guid.NewGuid().ToString("N");
        builder.Database = "";
        var originalConnection = Environment.GetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION");
        using (var admin = new MySqlConnection(builder.ConnectionString))
        {
            admin.Open();
            Execute(admin, "CREATE DATABASE `" + database + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci");
            try
            {
                builder.Database = database;
                using (var connection = new MySqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql");
                    var schema = File.ReadAllText(schemaPath);
                    Execute(connection, schema.Substring(schema.IndexOf("CREATE TABLE rol", StringComparison.Ordinal)));
                    SeedInventory(connection);
                }
                builder.AllowUserVariables = true;
                builder.MaximumPoolSize = 1;
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", builder.ConnectionString);
                CheckDatabaseSession();
                var today = DateTime.Today;
                var receipts = AppData.Receipts;
                var paidLines = receipts.Where(r => r.CreatedAt >= today && r.Status == "Pagado").SelectMany(r => r.Lines).ToList();
                var entries = AppData.InventoryMovements.Where(m => m.CreatedAt >= today && m.Type == "Entrada")
                    .GroupBy(m => m.ProductId).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
                var exits = paidLines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                var activity = Invoke(typeof(AppData), null, "GetInventoryActivity", today);
                var type = activity.GetType();
                var actualEntries = (Dictionary<string, int>)type.GetField("Entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(activity);
                var actualExits = (Dictionary<string, int>)type.GetField("Exits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(activity);
                Check(actualEntries.Count == entries.Count && entries.All(p => actualEntries[p.Key] == p.Value), "Las entradas deben coincidir con el cálculo anterior.");
                Check(actualExits.Count == exits.Count && exits.All(p => actualExits[p.Key] == p.Value), "Las salidas deben coincidir con el cálculo anterior.");
                foreach (var product in new[] { "p1", "p2" })
                    foreach (var option in new[] { "o1", "o2", "inexistente" })
                    {
                        const string name = "Producto · Variante";
                        var expected = paidLines.Where(l => l.ProductId == product &&
                            ((!string.IsNullOrWhiteSpace(l.OptionId) && l.OptionId == option) ||
                            (string.IsNullOrWhiteSpace(l.OptionId) && l.Name == name))).Sum(l => l.Quantity);
                        Check((int)Invoke(type, activity, "OptionExits", product, option, name) == expected,
                            "Debe conservar variantes por id, nombre antiguo y producto: " + product + "/" + option);
                    }
                Check(actualEntries["p1"] == 4 && actualEntries["o1"] == 2, "Debe excluir entradas antiguas, salidas y tipos con otra capitalización.");
                Check(actualExits["p1"] == 14 && actualExits["p2"] == 4, "Debe excluir ventas antiguas y reembolsos, conservar medianoche y fechas futuras.");
                AppData.SaveCustomer("12345678", "Cliente local", "Prueba", null, true);   // RNF-09: el DNI solo se guarda con consentimiento
                Task<DniLookupResult> lookup;
                var sync = typeof(AppData).GetField("Sync", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                lock (sync)
                {
                    lookup = DniLookupService.LookupAsync("12345678");
                    Check(!lookup.IsCompleted, "La consulta debe devolver el control aunque la base local esté ocupada.");
                }
                var localCustomer = lookup.GetAwaiter().GetResult();
                Check(localCustomer.Success && localCustomer.FullName == "CLIENTE LOCAL" && localCustomer.Source == "Registro local",
                    "Debe conservar la búsqueda local sin solicitar acceso al proveedor.");
                Console.WriteLine("SQL: contadores equivalentes al historial anterior en una base temporal aislada.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", originalConnection);
                MySqlConnection.ClearAllPools();
                Execute(admin, "DROP DATABASE `" + database + "`");
            }
        }
    }

    // RF-06 / RN-14 / RNF-09: migración de cliente (teléfono y consentimiento), regla de consentimiento en la capa de datos,
    // validación del teléfono y restauración de copias antiguas sin esas columnas. Usa una base temporal propia que borra al terminar.
    private static void CheckCustomerConsentDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDENPOS_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SQL omitido: configure EDENPOS_TEST_MYSQL_CONNECTION para incluir la prueba de clientes (RF-06).");
            return;
        }
        Check(Validation.IsValidPhone("") && Validation.IsValidPhone(null) && Validation.IsValidPhone("987654321"), "RF-06: teléfono vacío o celular de 9 dígitos.");
        Check(Validation.NormalizePhone("+51 987-654-321") == "987654321", "RF-06: debe aceptar el prefijo +51, espacios y guiones.");
        foreach (var invalid in new[] { "887654321", "98765432", "9876543210", "abc" })
            Check(!Validation.IsValidPhone(invalid), "RF-06: debe rechazar el teléfono " + invalid + ".");

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = "edenpos_test_" + Guid.NewGuid().ToString("N");
        builder.Database = "";
        var originalConnection = Environment.GetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION");
        using (var admin = new MySqlConnection(builder.ConnectionString))
        {
            admin.Open();
            Execute(admin, "CREATE DATABASE `" + database + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci");
            try
            {
                builder.Database = database;
                using (var connection = new MySqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    var schema = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql"));
                    Execute(connection, schema.Substring(schema.IndexOf("CREATE TABLE rol", StringComparison.Ordinal)));
                    // Base anterior a RF-06: sin las columnas nuevas y con un cliente ya guardado.
                    Execute(connection, "ALTER TABLE cliente DROP COLUMN telefono, DROP COLUMN consentimiento_datos, DROP COLUMN consentimiento_fecha");
                    Execute(connection, "INSERT INTO cliente (dni, nombre_completo, origen, created_at, updated_at) VALUES ('11111111', 'CLIENTE ANTIGUO', 'Prueba', NOW(), NOW())");
                }
                builder.AllowUserVariables = true;
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", builder.ConnectionString);
                Func<string, object> scalar = sql =>
                {
                    using (var connection = new MySqlConnection(builder.ConnectionString))
                    {
                        connection.Open();
                        using (var command = new MySqlCommand(sql, connection)) return command.ExecuteScalar();
                    }
                };
                Func<Action, string> throws = action => { try { action(); return null; } catch (ArgumentException ex) { return ex.Message; } };
                var columnsSql = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cliente' " +
                                 "AND COLUMN_NAME IN ('telefono', 'consentimiento_datos', 'consentimiento_fecha')";

                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3, "RF-06: la migración debe agregar telefono, consentimiento_datos y consentimiento_fecha.");
                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3, "RF-06: la migración debe ser idempotente.");
                var owner = AppData.Users.First(u => u.Role == UserRoles.Owner && u.Active);
                var login = AppData.TryLogin(owner.Id, "0000");
                Check(login.Outcome == AppData.LoginOutcome.Success, "RF-06: debe iniciar sesión la dueña de la base de prueba.");
                AppSession.SignIn(login.User);

                var old = AppData.FindCustomer("11111111");
                Check(old != null && !old.DataConsent && old.Phone == "" && old.ConsentDate == null, "RF-06: un cliente anterior queda sin consentimiento ni teléfono.");

                var message = throws(() => AppData.SaveCustomer("22222222", "Ana Pérez", "Prueba"));
                Check(message != null && message.Contains("consentimiento") && AppData.FindCustomer("22222222") == null, "RNF-09: sin consentimiento no se guarda el DNI.");
                Check(throws(() => AppData.SaveCustomer("22222222", "Ana Pérez", "Prueba", "987654321", false)) != null && AppData.FindCustomer("22222222") == null,
                    "RNF-09: sin consentimiento no se guarda el teléfono.");
                message = throws(() => AppData.SaveCustomer("22222222", "Ana Pérez", "Prueba", "123", true));
                Check(message != null && message.Contains("teléfono") && AppData.FindCustomer("22222222") == null, "RF-06: debe rechazar un teléfono inválido.");

                var start = DateTime.Now.AddSeconds(-2);
                var saved = AppData.SaveCustomer("22222222", "Ana Pérez", "Prueba", "+51 987 654 321", true);
                Check(saved.DataConsent && saved.Phone == "987654321" && saved.ConsentDate.HasValue && saved.ConsentDate.Value >= start,
                    "RF-06: con consentimiento guarda DNI, teléfono normalizado y fecha del consentimiento.");
                var firstConsent = saved.ConsentDate;
                System.Threading.Thread.Sleep(1100);
                var updated = AppData.SaveCustomer("22222222", "Ana Pérez Rojas", "Prueba", null, true);
                Check(updated.Phone == "987654321" && updated.FullName == "ANA PÉREZ ROJAS" && updated.ConsentDate == firstConsent,
                    "RF-06: sin teléfono nuevo conserva el anterior y la fecha del consentimiento original.");
                var cleared = AppData.SaveCustomer("22222222", "Ana Pérez Rojas", "Prueba", "", true);
                Check(cleared.Phone == "" && scalar("SELECT telefono FROM cliente WHERE dni = '22222222'") is DBNull, "RF-06: un teléfono vacío borra el anterior.");
                var upgraded = AppData.SaveCustomer("11111111", "Cliente Antiguo", "Prueba", "912345678", true);
                Check(upgraded.DataConsent && upgraded.ConsentDate.HasValue && upgraded.Phone == "912345678", "RF-06: un cliente anterior puede dar su consentimiento.");

                var lookup = DniLookupService.LookupAsync("11111111").GetAwaiter().GetResult();
                Check(lookup.Success && lookup.Source == "Registro local" && lookup.DataConsent && lookup.Phone == "912345678",
                    "RF-06: la consulta local devuelve teléfono y consentimiento.");
                scalar("INSERT INTO cliente (dni, nombre_completo, origen, telefono, created_at, updated_at) VALUES ('33333333', 'SIN PERMISO', 'Prueba', '955555555', NOW(), NOW())");
                lookup = DniLookupService.LookupAsync("33333333").GetAwaiter().GetResult();
                Check(lookup.Success && !lookup.DataConsent && lookup.Phone == "", "RNF-09: sin consentimiento la consulta no muestra el teléfono.");

                if (AppData.OpenShift == null) AppData.OpenNewShift(0m);
                message = throws(() => AppData.AddReceipt(new List<ReceiptLine> { new ReceiptLine { ProductId = "x", Name = "Prueba", UnitPrice = 800m, Quantity = 1 } },
                    "Ana", "", "Efectivo", 800m));
                Check(message != null && message.Contains("DNI"), "RN-11: una venta mayor a S/ 700 sin DNI sigue rechazada.");

                // Copia nueva (con las columnas) y copia "antigua" (sin ellas): ambas deben restaurarse.
                var current = Path.Combine(Path.GetTempPath(), "EdenPOS-rf06-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                var legacy = Path.Combine(Path.GetTempPath(), "EdenPOS-rf06-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                try
                {
                    AppData.ExportBackup(current);
                    File.Copy(current, legacy);
                    using (var zip = System.IO.Compression.ZipFile.Open(legacy, System.IO.Compression.ZipArchiveMode.Update))
                    {
                        var entry = zip.GetEntry("tables/cliente.json");
                        string json;
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
                        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var rows = serializer.Deserialize<List<Dictionary<string, object>>>(json);
                        Check(rows.Count == 3 && rows.All(r => r.ContainsKey("telefono") && r.ContainsKey("consentimiento_datos") && r.ContainsKey("consentimiento_fecha")),
                            "RF-06: la copia de seguridad incluye las columnas nuevas de cliente.");
                        foreach (var row in rows) { row.Remove("telefono"); row.Remove("consentimiento_datos"); row.Remove("consentimiento_fecha"); }
                        entry.Delete();
                        using (var writer = new StreamWriter(zip.CreateEntry("tables/cliente.json").Open(), new UTF8Encoding(false))) writer.Write(serializer.Serialize(rows));
                    }
                    AppData.ImportBackup(legacy);
                    var restoredLegacy = AppData.Customers;
                    Check(restoredLegacy.Count == 3 && restoredLegacy.All(c => !c.DataConsent && c.Phone == "" && c.ConsentDate == null),
                        "RF-06: una copia antigua sin las columnas se restaura (clientes sin consentimiento).");
                    AppData.ImportBackup(current);
                    var restored = AppData.FindCustomer("11111111");
                    Check(restored != null && restored.DataConsent && restored.Phone == "912345678" && restored.ConsentDate.HasValue,
                        "RF-06: una copia nueva restaura teléfono y consentimiento.");
                }
                finally
                {
                    if (File.Exists(current)) File.Delete(current);
                    if (File.Exists(legacy)) File.Delete(legacy);
                    AppSession.SignOut();
                }
                Console.WriteLine("SQL: registro de clientes con consentimiento (RF-06) en una base temporal aislada.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", originalConnection);
                MySqlConnection.ClearAllPools();
                Execute(admin, "DROP DATABASE `" + database + "`");
            }
        }
    }

    private static object Member(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null) return property.GetValue(target);
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
    }

    // RN-01 (saldo pendiente con autorización de la dueña y cobro posterior) y RF-17 (reporte de visitas): migración, validaciones en AppData,
    // caja esperada, reportes, reembolso, sexta visita, réplica y copias. Usa una base temporal propia que borra al terminar.
    private static void CheckPendingBalanceAndVisitsDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDENPOS_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SQL omitido: configure EDENPOS_TEST_MYSQL_CONNECTION para incluir la prueba de saldo pendiente y visitas (RN-01, RF-17).");
            return;
        }
        Check(AppData.FormatDuration(null) == "—" && AppData.FormatDuration(40) == "40 min" && AppData.FormatDuration(85.4) == "1 h 25 min",
            "RF-17: formato de la duración de la visita.");

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = "edenpos_test_" + Guid.NewGuid().ToString("N");
        builder.Database = "";
        var originalConnection = Environment.GetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION");
        var newColumns = new[] { "saldo_pendiente", "saldo_autorizado_por", "saldo_cobrado_at", "saldo_cobrado_medio", "saldo_cobrado_efectivo",
                                 "saldo_cobrado_yape", "saldo_cobrado_por", "saldo_id_sesion", "ingreso_at" };
        using (var admin = new MySqlConnection(builder.ConnectionString))
        {
            admin.Open();
            Execute(admin, "CREATE DATABASE `" + database + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci");
            try
            {
                builder.Database = database;
                using (var connection = new MySqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    var schema = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql"));
                    Execute(connection, schema.Substring(schema.IndexOf("CREATE TABLE rol", StringComparison.Ordinal)));
                    // Base anterior a RN-01 / RF-17: sin las columnas nuevas y con un recibo ya guardado.
                    Execute(connection, "ALTER TABLE recibo " + string.Join(", ", newColumns.Select(c => "DROP COLUMN " + c)));
                    Execute(connection, "INSERT INTO recibo (id_recibo, numero, created_at, empleado, cliente_nombre, medio_pago, monto_recibido, monto_efectivo, monto_yape, total, estado) " +
                                        "VALUES ('antiguo', 900, '2020-01-01 10:00:00', 'Prueba', 'Prueba', 'Efectivo', 10, 10, 0, 10, 'Pagado')");
                }
                builder.AllowUserVariables = true;
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", builder.ConnectionString);
                Func<string, object> scalar = sql =>
                {
                    using (var connection = new MySqlConnection(builder.ConnectionString))
                    {
                        connection.Open();
                        using (var command = new MySqlCommand(sql, connection)) return command.ExecuteScalar();
                    }
                };
                Func<Action, string> throws = action =>
                {
                    try { action(); return null; }
                    catch (ArgumentException ex) { return ex.Message; }
                    catch (InvalidOperationException ex) { return ex.Message; }
                };
                var columnsSql = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'recibo' AND COLUMN_NAME IN (" +
                                 string.Join(", ", newColumns.Select(c => "'" + c + "'")) + ")";

                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == newColumns.Length, "RN-01: la migración debe agregar las 9 columnas nuevas de recibo.");
                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == newColumns.Length, "RN-01: la migración debe ser idempotente.");
                var old = AppData.Receipts.Single(r => r.Id == "antiguo");
                Check(old.PendingBalance == 0m && old.CheckInAt == null && old.BalanceAuthorizedBy == null && old.OutstandingBalance == 0m && old.CashPaid == 10m,
                    "RN-01: un recibo anterior queda sin saldo pendiente ni hora de ingreso.");

                var owner = AppData.Users.First(u => u.Role == UserRoles.Owner && u.Active);
                var login = AppData.TryLogin(owner.Id, "0000");
                Check(login.Outcome == AppData.LoginOutcome.Success, "RN-01: debe iniciar sesión la dueña de la base de prueba.");
                AppSession.SignIn(login.User);
                var worker = AppData.SaveUser(null, "Cajera Prueba", "1234", UserRoles.Worker);
                AppSession.SignIn(AppData.Users.First(u => u.Id == worker.Id));
                AppData.OpenNewShift(100m);

                var sauna = AppData.Products.First(p => p.Category == "SAUNA" && p.Name == "AMIGOS X3");
                Func<List<ReceiptLine>> saunaLines = () => new List<ReceiptLine> { new ReceiptLine { ProductId = sauna.Id, Name = sauna.Name, UnitPrice = sauna.Price, Quantity = 1 } };
                var total = sauna.Price;   // 48
                Func<decimal, string, string, PendingBalanceAuthorization> auth = (amount, id, pin) =>
                    new PendingBalanceAuthorization { Amount = amount, OwnerUserId = id, OwnerPin = pin, Reason = "Pagará mañana por Yape" };
                var receiptsBefore = AppData.Receipts.Count;

                // Validaciones en AppData (no solo en la UI): nada se registra si fallan.
                var message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 28m, pendingBalance: auth(20m, owner.Id, "9999")));
                Check(message != null && message.Contains("PIN"), "RN-01: un PIN equivocado de la dueña rechaza el cobro.");
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 28m, pendingBalance: auth(20m, worker.Id, "1234")));
                Check(message != null && message.Contains("dueña"), "RN-01: un trabajador no puede autorizar un saldo pendiente.");
                var shortReason = auth(20m, owner.Id, "0000"); shortReason.Reason = "ab";
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 28m, pendingBalance: shortReason));
                Check(message != null && message.Contains("motivo"), "RN-01: el motivo de 3 a 160 caracteres es obligatorio.");
                var longReason = auth(20m, owner.Id, "0000"); longReason.Reason = new string('x', 161);
                Check(throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 28m, pendingBalance: longReason)) != null, "RN-01: el motivo no puede pasar de 160 caracteres.");
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 0m, pendingBalance: auth(total + 1m, owner.Id, "0000")));
                Check(message != null && message.Contains("mayor que el total"), "RN-01: el saldo no puede superar el total.");
                Check(throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 48m, pendingBalance: auth(0m, owner.Id, "0000"))) != null,
                    "RN-01: un saldo de cero no es un saldo pendiente.");
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 27m, pendingBalance: auth(20m, owner.Id, "0000")));
                Check(message != null && message.Contains("menor"), "RN-01: en efectivo se debe recibir al menos lo que se paga ahora.");
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Mixto", 0m, 10m, 10m, pendingBalance: auth(20m, owner.Id, "0000")));
                Check(message != null && message.Contains("sumar exactamente"), "RN-01: pagado + saldo debe ser igual al total.");
                message = throws(() => AppData.AddReceipt(saunaLines(), "Ana", "", "Efectivo", 40m));
                Check(message != null, "RN-01: sin autorización la visita no termina con saldo (pago incompleto rechazado).");
                Check(AppData.Receipts.Count == receiptsBefore && Convert.ToInt32(scalar("SELECT COUNT(*) FROM auditoria WHERE accion = 'SALDO_PENDIENTE_AUTORIZADO'")) == 0,
                    "RN-01: un cobro rechazado no deja recibo ni auditoría.");

                // Comprobación previa del diálogo: cuenta los intentos fallidos como el inicio de sesión.
                var check = AppData.CheckOwnerAuthorization(owner.Id, "1111");
                Check(check != null && check.Contains("PIN incorrecto") && Convert.ToInt32(scalar("SELECT intentos_fallidos FROM usuario WHERE id_usuario = " + owner.Id)) == 1,
                    "RN-01: un PIN equivocado en la autorización cuenta como intento fallido.");
                Check(AppData.CheckOwnerAuthorization(owner.Id, "0000") == null && Convert.ToInt32(scalar("SELECT intentos_fallidos FROM usuario WHERE id_usuario = " + owner.Id)) == 0,
                    "RN-01: el PIN correcto autoriza y reinicia el contador.");
                Check(AppData.CheckOwnerAuthorization(worker.Id, "1234") != null, "RN-01: un trabajador no aparece como autorizador válido.");
                Check(AppData.ActiveOwners.Count == 1 && AppData.ActiveOwners[0].Id == owner.Id, "RN-01: solo las dueñas activas pueden autorizar.");

                // Visita desde un ticket guardado con llave, con saldo pendiente autorizado.
                var ticket = AppData.SaveTicket(saunaLines(), "Ana Pérez", "", "Ana", 5);
                scalar("UPDATE ticket_guardado SET created_at = DATE_SUB(NOW(), INTERVAL 90 MINUTE) WHERE id_ticket = '" + ticket.Id + "'");
                var visit = AppData.AddReceipt(saunaLines(), "Ana Pérez", "", "Efectivo", 30m, savedTicketId: ticket.Id,
                    lossReport: new LossReport { ReturnedKeys = new List<int> { 5 } }, pendingBalance: auth(20m, owner.Id, "0000"));
                var stored = AppData.Receipts.Single(r => r.Id == visit.Id);
                Check(stored.Total == total && stored.PendingBalance == 20m && stored.PaidAtVisit == 28m && stored.CashAmount == 28m && stored.CashPaid == 28m &&
                      stored.Status == "Pagado" && stored.BalanceAuthorizedBy == owner.Name && stored.OutstandingBalance == 20m,
                    "RN-01: el recibo guarda el total, lo pagado y el saldo autorizado (pagado + saldo = total).");
                Check(stored.CheckInAt.HasValue && Math.Abs((stored.CreatedAt - stored.CheckInAt.Value).TotalMinutes - 90) < 2,
                    "RF-17: ingreso_at guarda la hora de creación del ticket.");
                Check(Convert.ToString(scalar("SELECT detalle FROM auditoria WHERE accion = 'SALDO_PENDIENTE_AUTORIZADO'")).Contains("Pagará mañana por Yape") &&
                      Convert.ToString(scalar("SELECT detalle FROM auditoria WHERE accion = 'SALDO_PENDIENTE_AUTORIZADO'")).Contains(owner.Name),
                    "RN-01: la autorización queda en Auditoría con la dueña y el motivo.");
                Check(Convert.ToDecimal(scalar("SELECT c.id_recibo IS NOT NULL FROM comprobante c WHERE c.id_recibo = '" + visit.Id + "'")) == 1m && visit.Comprobante.Total == total,
                    "RN-01: el comprobante simulado se emite por el total.");
                var shift = AppData.OpenShift;
                Check(AppData.CalculateExpectedCash(shift) == 128m, "RN-01: la caja esperada suma solo lo pagado (100 + 28).");

                // Venta que no es visita (sin ticket ni SAUNA) y cinco visitas del mismo DNI, una de ellas con todo el saldo pendiente.
                AppData.AddReceipt(new List<ReceiptLine> { new ReceiptLine { ProductId = null, Name = "Gaseosa", UnitPrice = 5m, Quantity = 1 } }, "Público", "", "Yape", 0m);
                var fullPending = AppData.AddReceipt(saunaLines(), "Luis", "44444444", "Efectivo", 0m, pendingBalance: auth(total, owner.Id, "0000"));
                Check(fullPending.PaidAtVisit == 0m && fullPending.CashAmount == 0m && fullPending.PendingBalance == total, "RN-01: se puede dejar todo el total pendiente.");
                for (var i = 0; i < 4; i++) AppData.AddReceipt(saunaLines(), "Luis", "44444444", "Yape", 0m);
                var promotion = AppData.CalculatePromotion("44444444", saunaLines());
                Check(promotion.PromocionId == "SEXTA_VISITA", "RN-03: un recibo con saldo pendiente cuenta como visita para la sexta visita gratis.");
                Check(AppData.CalculateExpectedCash(shift) == 128m, "RN-01: Yape y saldos no cobrados no cambian la caja esperada.");

                var today = DateTime.Today;
                var sales = AppData.GetSalesReport(today, today);
                var cashRow = sales.ByPayment.Single(r => r.Method == "Efectivo");
                Check(sales.TotalSold == total * 6 + 5m && sales.PendingBalance == 20m + total && sales.PendingCount == 2 && cashRow.Cash == 28m && cashRow.PendingBalance == 20m + total,
                    "RN-01: el reporte de ventas separa el saldo pendiente y cuenta solo lo pagado en efectivo.");

                // Cobro posterior del saldo.
                message = throws(() => AppData.CollectBalance(visit.Id, "Mixto", 0m, 15m, 10m));
                Check(message != null && message.Contains("exactamente"), "RN-01: el cobro del saldo debe ser por el saldo completo.");
                var collected = AppData.CollectBalance(visit.Id, "Mixto", 0m, 12m, 8m);
                Check(collected.BalanceCollectedAt.HasValue && collected.BalanceCollectedCash == 12m && collected.BalanceCollectedYape == 8m &&
                      collected.OutstandingBalance == 0m && collected.TotalPaid == total && collected.PendingBalance == 20m && collected.BalanceCollectedBy == AppData.Users.First(u => u.Id == worker.Id).Name,
                    "RN-01: el saldo cobrado queda registrado y el recibo ya no debe nada.");
                Check(Convert.ToString(scalar("SELECT saldo_id_sesion FROM recibo WHERE id_recibo = '" + visit.Id + "'")) == shift.Id, "RN-01: el cobro del saldo queda en el turno abierto.");
                Check(Convert.ToInt32(scalar("SELECT COUNT(*) FROM auditoria WHERE accion = 'SALDO_COBRADO'")) == 1, "RN-01: el cobro del saldo queda en Auditoría.");
                Check(AppData.CalculateExpectedCash(shift) == 140m, "RN-01: el efectivo del saldo entra a la caja abierta (128 + 12).");
                var receiptsNow = AppData.Receipts;
                Check(Receipt.CashBetween(receiptsNow, today, today.AddDays(1)) == 40m && Receipt.YapeBetween(receiptsNow, today, today.AddDays(1)) == 5m + total * 4 + 8m,
                    "RN-01: el resumen del día incluye el saldo cobrado.");
                Check(throws(() => AppData.CollectBalance(visit.Id, "Efectivo", 100m)) != null, "RN-01: un saldo no se cobra dos veces.");
                sales = AppData.GetSalesReport(today, today);
                Check(sales.PendingBalance == total && sales.PendingCount == 1 && sales.CollectedCount == 1 && sales.Collected == 20m && sales.CollectedCash == 12m,
                    "RN-01: el reporte de ventas muestra el saldo cobrado aparte y lo que aún se debe.");

                // Sin turno abierto no se cobra el saldo.
                AppData.CloseShift(shift.Id, 140m, null);
                message = throws(() => AppData.CollectBalance(fullPending.Id, "Efectivo", total));
                Check(message != null && message.Contains("turno"), "RN-01: el saldo solo se cobra con un turno abierto.");
                AppData.OpenNewShift(0m);

                // Visitas (RF-17): el ticket con llave y las cinco ventas SAUNA; la gaseosa no es visita.
                var visits = AppData.GetVisitReport(today, today);
                Check(visits.Visits == 6 && visits.Adults == 18 && visits.Children == 0 && visits.People == 18 && visits.ByDay.Count == 1 && visits.ByDay[0].Visits == 6,
                    "RF-17: visitas y personas del rango (AMIGOS X3 = 3 adultos).");
                Check(visits.TimedVisits == 1 && visits.AverageMinutes.HasValue && Math.Abs(visits.AverageMinutes.Value - 90) < 2, "RF-17: duración promedio desde el ingreso.");
                Check(visits.PendingBalance == total && visits.PendingCount == 1 && visits.Total == total * 6, "RF-17: saldo pendiente total de las visitas.");
                Check(AppData.GetVisitReport(today.AddDays(-3), today.AddDays(-1)).Visits == 0, "RF-17: el rango de fechas filtra las visitas.");

                // Reembolso: devuelve solo lo pagado y anula la deuda.
                AppSession.SignIn(AppData.Users.First(u => u.Id == owner.Id));
                AppData.RefundReceipt(AppData.Receipts.Single(r => r.Id == fullPending.Id), "Cliente no volvió");
                var refunded = AppData.Receipts.Single(r => r.Id == fullPending.Id);
                Check(refunded.Status == "Reembolsado" && refunded.OutstandingBalance == 0m && refunded.TotalPaid == 0m, "RN-01: el reembolso anula el saldo y no devuelve lo no pagado.");
                var refundDetail = Convert.ToString(scalar("SELECT detalle FROM auditoria WHERE accion = 'RECIBO_REEMBOLSADO' ORDER BY id_auditoria DESC LIMIT 1"));
                Check(refundDetail.Contains("devuelto 0.00") && refundDetail.Contains("anulado"), "RN-01: la auditoría del reembolso indica lo devuelto y el saldo anulado.");
                Check(throws(() => AppData.CollectBalance(fullPending.Id, "Efectivo", total)) != null, "RN-01: el saldo de un recibo reembolsado no se cobra.");
                visits = AppData.GetVisitReport(today, today);
                Check(visits.Visits == 5 && visits.PendingBalance == 0m, "RF-17: un recibo reembolsado no cuenta como visita.");

                // Réplica en Supabase: las columnas nuevas de recibo se quedan en la base local.
                var tables = (System.Collections.IEnumerable)Invoke(typeof(AppData), null, "ReplicaBuildTables");
                foreach (var info in tables)
                {
                    var table = Member(info, "Table");
                    if ((string)Member(table, "Name") != "recibo") continue;
                    var columns = (string[])Member(table, "Columns");
                    Check(columns.Contains("total") && !columns.Any(c => newColumns.Contains(c)), "RN-01: la réplica omite las columnas nuevas de recibo.");
                }

                // Copia nueva (con las columnas) y copia "antigua" (sin ellas): ambas deben restaurarse.
                var current = Path.Combine(Path.GetTempPath(), "EdenPOS-rn01-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                var legacy = Path.Combine(Path.GetTempPath(), "EdenPOS-rn01-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                try
                {
                    AppData.ExportBackup(current);
                    File.Copy(current, legacy);
                    using (var zip = System.IO.Compression.ZipFile.Open(legacy, System.IO.Compression.ZipArchiveMode.Update))
                    {
                        var entry = zip.GetEntry("tables/recibo.json");
                        string json;
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
                        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var rows = serializer.Deserialize<List<Dictionary<string, object>>>(json);
                        Check(rows.Count == 8 && rows.All(r => newColumns.All(r.ContainsKey)), "RN-01: la copia de seguridad incluye las columnas nuevas de recibo.");
                        foreach (var row in rows) foreach (var column in newColumns) row.Remove(column);
                        entry.Delete();
                        using (var writer = new StreamWriter(zip.CreateEntry("tables/recibo.json").Open(), new UTF8Encoding(false))) writer.Write(serializer.Serialize(rows));
                    }
                    AppData.ImportBackup(legacy);
                    var restoredLegacy = AppData.Receipts;
                    Check(restoredLegacy.Count == 8 && restoredLegacy.All(r => r.PendingBalance == 0m && r.CheckInAt == null && r.BalanceCollectedAt == null),
                        "RN-01: una copia antigua sin las columnas se restaura con saldo 0.");
                    AppData.ImportBackup(current);
                    var restored = AppData.Receipts.Single(r => r.Id == visit.Id);
                    Check(restored.PendingBalance == 20m && restored.BalanceCollectedCash == 12m && restored.CheckInAt.HasValue && restored.BalanceAuthorizedBy == owner.Name,
                        "RN-01: una copia nueva restaura el saldo, su cobro y la hora de ingreso.");
                }
                finally
                {
                    if (File.Exists(current)) File.Delete(current);
                    if (File.Exists(legacy)) File.Delete(legacy);
                    AppSession.SignOut();
                }
                Console.WriteLine("SQL: saldo pendiente (RN-01) y reporte de visitas (RF-17) en una base temporal aislada.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", originalConnection);
                MySqlConnection.ClearAllPools();
                Execute(admin, "DROP DATABASE `" + database + "`");
            }
        }
    }

    private static void Execute(MySqlConnection connection, string sql)
    {
        using (var command = new MySqlCommand(sql, connection)) command.ExecuteNonQuery();
    }

    private static void SeedInventory(MySqlConnection connection)
    {
        using (var command = new MySqlCommand(@"
            INSERT INTO recibo (id_recibo, numero, created_at, empleado, cliente_nombre, medio_pago,
                monto_recibido, monto_efectivo, monto_yape, total, estado) VALUES
                ('antiguo',1,@ayer,'Prueba','Prueba','Efectivo',10,10,0,10,'Pagado'),
                ('medianoche',2,@hoy,'Prueba','Prueba','Efectivo',10,10,0,10,'Pagado'),
                ('actual',3,@ahora,'Prueba','Prueba','Yape',10,0,10,10,'Pagado'),
                ('futuro',4,@futuro,'Prueba','Prueba','Mixto',10,5,5,10,'Pagado'),
                ('reembolso',5,@ahora,'Prueba','Prueba','Efectivo',10,10,0,10,'Reembolsado'),
                ('minusculas',6,@ahora,'Prueba','Prueba','Efectivo',10,10,0,10,'pagado');
            INSERT INTO recibo_linea (id_recibo, orden, id_producto, id_variante, nombre, precio_unitario, cantidad) VALUES
                ('antiguo',1,'p1','o1','Antiguo',10,100),
                ('medianoche',1,'p1','o1','Nombre modificado',10,2),
                ('actual',1,'p1',NULL,'Producto · Variante',10,3),
                ('actual',2,'p1',' ','Producto · Variante',10,5),
                ('actual',3,'p1','o2','Otra variante',10,1),
                ('actual',4,'p2','o1','Otro producto',10,4),
                ('actual',5,'p1',NULL,'producto · variante',10,1),
                ('futuro',1,'p1','o1','Futuro',10,2),
                ('reembolso',1,'p1','o1','Reembolsado',10,200),
                ('minusculas',1,'p1','o1','Estado diferente',10,300);
            INSERT INTO movimiento_inventario (id_movimiento, id_producto, id_variante, producto_nombre, tipo,
                cantidad, stock_previo, stock_nuevo, nota, created_at) VALUES
                ('m1','p1',NULL,'Producto','Entrada',4,0,4,'',@hoy),
                ('m2','p1','o1','Variante','Entrada',2,0,2,'',@ahora),
                ('m3','p1',NULL,'Producto','Entrada',100,0,100,'',@ayer),
                ('m4','p1',NULL,'Producto','Salida',20,20,0,'',@ahora),
                ('m5','p1',NULL,'Producto','entrada',300,0,300,'',@ahora);", connection))
        {
            command.Parameters.AddWithValue("@hoy", DateTime.Today);
            command.Parameters.AddWithValue("@ayer", DateTime.Today.AddSeconds(-1));
            command.Parameters.AddWithValue("@ahora", DateTime.Today.AddHours(12));
            command.Parameters.AddWithValue("@futuro", DateTime.Today.AddDays(1));
            command.ExecuteNonQuery();
        }
    }
}
