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
            CheckCashRegisterDatabase();
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

    // RN-02, RF-04, RF-05, RN-09 y RNF-04 (bloque Caja): sin turno no hay cobro, medio de los movimientos, cierre por medio de pago con
    // observación obligatoria si cualquiera de los dos no cuadra, auditoría de todo cierre, reportes, migración y copias antiguas/nuevas.
    // Usa una base temporal propia que borra al terminar.
    private static void CheckCashRegisterDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDENPOS_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SQL omitido: configure EDENPOS_TEST_MYSQL_CONNECTION para incluir la prueba de caja (RN-02, RF-04, RF-05).");
            return;
        }
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
                    // Base anterior a 007: sin las columnas nuevas, con un turno cerrado, un movimiento y un recibo antiguo sin turno (importación de datos legados).
                    Execute(connection, "ALTER TABLE movimiento_caja DROP COLUMN medio");
                    Execute(connection, "ALTER TABLE sesion_caja DROP COLUMN yape_esperado, DROP COLUMN yape_contado");
                    Execute(connection, "INSERT INTO sesion_caja (id_sesion, numero, empleado, fecha_apertura, fecha_cierre, monto_inicial, monto_cierre, monto_esperado, abierta) " +
                                        "VALUES ('turnoantiguo', 1, 'Prueba', '2020-01-01 08:00:00', '2020-01-01 20:00:00', 50, 70, 60, 0)");
                    Execute(connection, "INSERT INTO movimiento_caja (id_movimiento, id_sesion, empleado, tipo, importe, comentario, created_at) " +
                                        "VALUES ('movantiguo', 'turnoantiguo', 'Prueba', 'Depósito', 10, 'Antiguo', '2020-01-01 09:00:00')");
                    Execute(connection, "INSERT INTO recibo (id_recibo, numero, id_sesion, created_at, empleado, cliente_nombre, medio_pago, monto_recibido, monto_efectivo, monto_yape, total, estado) " +
                                        "VALUES ('legado', 900, NULL, '2019-06-01 10:00:00', 'Prueba', 'Prueba', 'Efectivo', 5, 5, 0, 5, 'Pagado')");
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
                Func<Action, Exception> error = action => { try { action(); return null; } catch (Exception ex) { return ex; } };
                var columnsSql = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND " +
                                 "((TABLE_NAME = 'movimiento_caja' AND COLUMN_NAME = 'medio') OR (TABLE_NAME = 'sesion_caja' AND COLUMN_NAME IN ('yape_esperado', 'yape_contado')))";

                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3, "RF-04/RF-05: la migración debe agregar movimiento_caja.medio, sesion_caja.yape_esperado y yape_contado.");
                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3, "RF-04/RF-05: la migración debe ser idempotente.");
                Check(AppData.CashMovements.Single().Medium == CashMedia.Efectivo, "RF-04: un movimiento anterior queda como Efectivo.");
                var oldShift = AppData.Shifts.Single();
                Check(oldShift.ExpectedYape == null && oldShift.CountedYape == null, "RF-05: un turno anterior queda sin Yape esperado ni confirmado.");

                var owner = AppData.Users.First(u => u.Role == UserRoles.Owner && u.Active);
                var login = AppData.TryLogin(owner.Id, "0000");
                Check(login.Outcome == AppData.LoginOutcome.Success, "Caja: debe iniciar sesión la dueña de la base de prueba.");
                AppSession.SignIn(login.User);
                try
                {
                    Func<string, decimal, decimal, decimal, Receipt> sell = (method, total, cash, yape) => AppData.AddReceipt(
                        new List<ReceiptLine> { new ReceiptLine { ProductId = "x", Name = "Ingreso", UnitPrice = total, Quantity = 1 } },
                        "Prueba", "", method, method == "Yape" ? 0m : cash, method == "Mixto" ? cash : -1m, method == "Mixto" ? yape : -1m);

                    // RN-02: sin turno abierto no se cobra y no se escribe nada.
                    var receiptsBefore = Convert.ToInt32(scalar("SELECT COUNT(*) FROM recibo"));
                    var numberBefore = Convert.ToInt32(scalar("SELECT next_receipt_number FROM configuracion"));
                    var noShift = error(() => sell("Efectivo", 30m, 30m, 0m));
                    Check(noShift is InvalidOperationException && noShift.Message == AppData.NoOpenShiftMessage, "RN-02: sin turno, AddReceipt rechaza el cobro con un mensaje claro.");
                    Check(Convert.ToInt32(scalar("SELECT COUNT(*) FROM recibo")) == receiptsBefore && Convert.ToInt32(scalar("SELECT next_receipt_number FROM configuracion")) == numberBefore,
                        "RN-02: el cobro rechazado no guarda recibo ni consume número.");
                    Check(AppData.Receipts.Any(r => r.Id == "legado"), "RN-02: los recibos legados sin turno siguen visibles.");

                    var shift = AppData.OpenNewShift(100m);
                    var cashReceipt = sell("Efectivo", 30m, 30m, 0m);
                    var yapeReceipt = sell("Yape", 20m, 0m, 20m);
                    sell("Mixto", 50m, 10m, 40m);
                    Check(Convert.ToString(scalar("SELECT id_sesion FROM recibo WHERE id_recibo = '" + cashReceipt.Id + "'")) == shift.Id, "RN-02: el recibo queda ligado al turno abierto.");

                    // RF-04: medio de los movimientos; solo el efectivo cambia el efectivo esperado.
                    Check(error(() => AppData.AddCashMovement("Depósito", 5m, "Prueba", "Tarjeta")) is ArgumentException, "RF-04: rechaza un medio distinto de Efectivo o Yape.");
                    AppData.AddCashMovement("Depósito", 5m, "Sencillo para vuelto");
                    AppData.AddCashMovement("Salida", 3m, "Pago a proveedor por Yape", CashMedia.Yape);
                    Check(Convert.ToString(scalar("SELECT medio FROM movimiento_caja WHERE importe = -3")) == "Yape", "RF-04: el medio Yape se guarda en movimiento_caja.");
                    Check(AppData.CashMovements.Count(m => m.ShiftId == shift.Id && m.Medium == CashMedia.Yape) == 1, "RF-04: CashMovements lee el medio.");

                    var balance = AppData.GetShiftBalance(shift);
                    Check(balance.CashSales == 40m && balance.YapeSales == 60m && balance.CashMovements == 5m && balance.YapeMovements == -3m,
                        "RF-05: desglose por medio (ventas y movimientos de efectivo y de Yape).");
                    Check(balance.ExpectedCash == 145m && AppData.CalculateExpectedCash(shift) == 145m, "RF-04: el movimiento por Yape no cambia el efectivo esperado.");
                    Check(balance.ExpectedYape == 57m && AppData.CalculateExpectedYape(shift) == 57m, "RF-05: Yape esperado = ventas Yape + parte Yape de mixtas + movimientos Yape.");
                    Check(balance.ProjectedAvailable == 202m, "RF-05: proyección del disponible = efectivo esperado + Yape esperado.");
                    AppData.RefundReceipt(yapeReceipt, "Cliente se arrepintió");
                    Check(AppData.CalculateExpectedYape(shift) == 37m && AppData.CalculateExpectedCash(shift) == 145m, "RF-05: un reembolso de Yape sale del Yape esperado, no del efectivo.");

                    // RN-02 con dos cajas: si otra caja está cerrando el turno, el cobro espera y luego lo rechaza (no queda una venta fuera del cierre).
                    using (var other = new MySqlConnection(builder.ConnectionString))
                    {
                        other.Open();
                        using (var transaction = other.BeginTransaction())
                        {
                            using (var command = new MySqlCommand("SELECT id_sesion FROM sesion_caja WHERE id_sesion = @id FOR UPDATE", other, transaction))
                            {
                                command.Parameters.AddWithValue("@id", shift.Id);
                                command.ExecuteScalar();
                            }
                            using (var command = new MySqlCommand("UPDATE sesion_caja SET abierta = 0 WHERE id_sesion = @id", other, transaction))
                            {
                                command.Parameters.AddWithValue("@id", shift.Id);
                                command.ExecuteNonQuery();
                            }
                            var pending = Task.Run(() => error(() => sell("Efectivo", 7m, 7m, 0m)));
                            Check(!pending.Wait(1500), "RN-02: el cobro espera mientras otra caja cierra el turno (LOCK IN SHARE MODE).");
                            transaction.Commit();
                            var raced = pending.Result;
                            Check(raced is InvalidOperationException && raced.Message == AppData.NoOpenShiftMessage, "RN-02: tras el cierre en otra caja, el cobro en espera se rechaza.");
                        }
                        Execute(other, "UPDATE sesion_caja SET abierta = 1 WHERE id_sesion = '" + shift.Id + "'");
                    }
                    Check(Convert.ToInt32(scalar("SELECT COUNT(*) FROM recibo WHERE total = 7")) == 0, "RN-02: el cobro rechazado en la carrera no se guardó.");

                    // RF-05 / RN-09: diferencia solo en Yape -> observación obligatoria; sin ella el turno sigue abierto.
                    var missing = error(() => AppData.CloseShift(shift.Id, 145m, 30m, null));
                    Check(missing is InvalidOperationException && missing.Message.Contains("Yape") && AppData.OpenShift != null,
                        "RN-09: una diferencia solo de Yape exige observación y no cierra el turno.");
                    Check(error(() => AppData.CloseShift(shift.Id, 140m, 37m, "")) is InvalidOperationException && AppData.OpenShift != null,
                        "RN-09: una diferencia de efectivo sigue exigiendo observación.");
                    var closed = AppData.CloseShift(shift.Id, 145m, 30m, "Falta confirmar un Yape");
                    Check(closed != null && closed.ExpectedAmount == 145m && closed.ExpectedYape == 37m && closed.CountedYape == 30m && closed.ClosingNote == "Falta confirmar un Yape",
                        "RF-05: CloseShift devuelve los montos guardados.");
                    Check(AppData.OpenShift == null && Convert.ToDecimal(scalar("SELECT yape_esperado FROM sesion_caja WHERE id_sesion = '" + shift.Id + "'")) == 37m &&
                          Convert.ToDecimal(scalar("SELECT yape_contado FROM sesion_caja WHERE id_sesion = '" + shift.Id + "'")) == 30m &&
                          Convert.ToDecimal(scalar("SELECT monto_esperado FROM sesion_caja WHERE id_sesion = '" + shift.Id + "'")) == 145m,
                        "RF-05: sesion_caja guarda efectivo esperado, Yape esperado y Yape confirmado.");
                    var audit = AppData.GetAuditoria(DateTime.Today, DateTime.Today, null);
                    var closeEntry = audit.FirstOrDefault(a => a.Action == AuditActions.CajaCerrada);
                    Check(closeEntry != null && closeEntry.Detail.Contains("efectivo esperado 145.00, contado 145.00") && closeEntry.Detail.Contains("Yape esperado 37.00, confirmado 30.00"),
                        "RNF-04: el cierre se audita con esperado y contado de ambos medios.");
                    var diffEntry = audit.FirstOrDefault(a => a.Action == AuditActions.CajaDiferencia);
                    Check(diffEntry != null && diffEntry.Detail.Contains("de Yape -7.00") && diffEntry.Detail.Contains("Falta confirmar"), "RNF-04: con diferencia se mantiene CAJA_DIFERENCIA.");
                    Check((string)Invoke(typeof(AuditView), null, "ActionName", AuditActions.CajaCerrada) == "Cierre de caja" && (Tone)Invoke(typeof(AuditView), null, "ActionTone", AuditActions.CajaCerrada) == Tone.Info,
                        "RNF-04: la pantalla de Auditoría nombra el cierre de caja.");

                    // Un cierre que cuadra también se audita (sin CAJA_DIFERENCIA) y no pide observación.
                    System.Threading.Thread.Sleep(1100);
                    var second = AppData.OpenNewShift(0m);
                    Check(AppData.CloseShift(second.Id, 0m, 0m, null) != null && AppData.CloseShift(second.Id, 0m, 0m, null) == null, "RF-05: un turno ya cerrado no se vuelve a cerrar.");
                    audit = AppData.GetAuditoria(DateTime.Today, DateTime.Today, null);
                    Check(audit.Count(a => a.Action == AuditActions.CajaCerrada) == 2 && audit.Count(a => a.Action == AuditActions.CajaDiferencia) == 1,
                        "RNF-04: todo cierre se audita; CAJA_DIFERENCIA solo si no cuadra.");

                    // Reportes -> Caja: columnas de Yape (los turnos antiguos quedan sin ellas).
                    var report = AppData.GetShiftReport(DateTime.Today, DateTime.Today);
                    var row = report.Rows.First(r => r.Number == shift.Number);
                    Check(row.ExpectedYape == 37m && row.CountedYape == 30m && row.YapeDifference == -7m && row.Difference == 0m, "RF-05: el reporte de caja incluye Yape esperado, contado y diferencia.");
                    Check(report.YapeExpectedTotal == 37m && report.YapeCountedTotal == 30m && report.YapeDifferenceTotal == -7m, "RF-05: totales de Yape del reporte de caja.");
                    var oldRow = AppData.GetShiftReport(new DateTime(2020, 1, 1), new DateTime(2020, 1, 1)).Rows.Single();
                    Check(oldRow.ExpectedYape == null && oldRow.YapeDifference == null && oldRow.Difference == 10m, "RF-05: un turno anterior sigue en el reporte sin datos de Yape.");

                    // Réplica de Supabase: las columnas nuevas se quedan en la base local (igual que observacion_cierre).
                    var tables = (System.Collections.IEnumerable)Invoke(typeof(AppData), null, "ReplicaBuildTables");
                    foreach (var info in tables)
                    {
                        var table = info.GetType().GetField("Table").GetValue(info);
                        var name = (string)table.GetType().GetField("Name").GetValue(table);
                        var columns = (string[])table.GetType().GetField("Columns").GetValue(table);
                        if (name == "sesion_caja") Check(!columns.Contains("yape_esperado") && !columns.Contains("yape_contado") && columns.Contains("monto_esperado"), "RF-05: la réplica omite las columnas de Yape del cierre.");
                        if (name == "movimiento_caja") Check(!columns.Contains("medio") && columns.Contains("importe"), "RF-04: la réplica omite el medio del movimiento.");
                    }

                    // Copia nueva (con las columnas) y copia "antigua" (sin ellas): ambas deben restaurarse.
                    var current = Path.Combine(Path.GetTempPath(), "EdenPOS-caja-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                    var legacy = Path.Combine(Path.GetTempPath(), "EdenPOS-caja-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                    try
                    {
                        AppData.ExportBackup(current);
                        File.Copy(current, legacy);
                        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                        using (var zip = System.IO.Compression.ZipFile.Open(legacy, System.IO.Compression.ZipArchiveMode.Update))
                        {
                            foreach (var pair in new[] { new[] { "sesion_caja", "yape_esperado", "yape_contado" }, new[] { "movimiento_caja", "medio" } })
                            {
                                var entry = zip.GetEntry("tables/" + pair[0] + ".json");
                                string json;
                                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
                                var rows = serializer.Deserialize<List<Dictionary<string, object>>>(json);
                                Check(rows.Count > 0 && rows.All(r => pair.Skip(1).All(r.ContainsKey)), "Caja: la copia de seguridad incluye las columnas nuevas de " + pair[0] + ".");
                                foreach (var item in rows) foreach (var column in pair.Skip(1)) item.Remove(column);
                                entry.Delete();
                                using (var writer = new StreamWriter(zip.CreateEntry("tables/" + pair[0] + ".json").Open(), new UTF8Encoding(false))) writer.Write(serializer.Serialize(rows));
                            }
                        }
                        AppData.ImportBackup(legacy);
                        Check(AppData.CashMovements.All(m => m.Medium == CashMedia.Efectivo) && AppData.Shifts.All(s => s.ExpectedYape == null && s.CountedYape == null),
                            "Caja: una copia antigua se restaura con los valores por defecto (Efectivo, sin Yape).");
                        AppData.ImportBackup(current);
                        var restored = AppData.Shifts.First(s => s.Id == shift.Id);
                        Check(restored.ExpectedYape == 37m && restored.CountedYape == 30m && AppData.CashMovements.Count(m => m.Medium == CashMedia.Yape) == 1,
                            "Caja: una copia nueva restaura el medio de los movimientos y el Yape del cierre.");
                    }
                    finally
                    {
                        if (File.Exists(current)) File.Delete(current);
                        if (File.Exists(legacy)) File.Delete(legacy);
                    }
                }
                finally
                {
                    AppSession.SignOut();
                }
                Console.WriteLine("SQL: caja por medio de pago (RN-02, RF-04, RF-05, RNF-04) en una base temporal aislada.");
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
