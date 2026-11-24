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
            CheckComprobantesTipoConsentimientoDatabase();
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

                    // Réplica de Supabase: las columnas nuevas se replican si Supabase las tiene; las de seguridad nunca.
                    var tables = (System.Collections.IEnumerable)Invoke(typeof(AppData), null, "ReplicaBuildTables");
                    foreach (var info in tables)
                    {
                        var table = info.GetType().GetField("Table").GetValue(info);
                        var name = (string)table.GetType().GetField("Name").GetValue(table);
                        var columns = (string[])table.GetType().GetField("Columns").GetValue(table);
                        if (name == "sesion_caja")
                        {
                            Check(columns.Contains("yape_esperado") && columns.Contains("yape_contado") && columns.Contains("monto_esperado"), "RF-05: la réplica incluye el Yape del cierre cuando Supabase lo admite.");
                            var missingCols = new List<string>();
                            var oldRemote = AppData.ReplicaSelectColumns("sesion_caja", columns, columns.Where(c => c != "yape_esperado" && c != "yape_contado" && c != "observacion_cierre").ToList(), missingCols);
                            Check(!oldRemote.Contains("yape_esperado") && oldRemote.Contains("monto_esperado") && missingCols.Contains("sesion_caja.yape_contado") && missingCols.Count == 3,
                                "RF-05: con un esquema de Supabase anterior, el Yape del cierre no se replica y queda en el aviso.");
                        }
                        if (name == "movimiento_caja") Check(columns.Contains("medio") && columns.Contains("importe"), "RF-04: la réplica incluye el medio del movimiento.");
                        if (name == "configuracion") Check(!columns.Contains("dni_api_token_protected"), "Réplica: el token de DNI nunca se replica.");
                        if (name == "usuario")
                        {
                            Check(!columns.Contains("intentos_fallidos") && !columns.Contains("bloqueado_en"), "Réplica: el bloqueo de cuentas nunca se replica.");
                            var none = new List<string>();
                            Check(!AppData.ReplicaSelectColumns("usuario", new[] { "id_usuario", "intentos_fallidos" }, new[] { "id_usuario", "intentos_fallidos" }, none).Contains("intentos_fallidos") && none.Count == 0,
                                "Réplica: el bloqueo no se replica aunque Supabase tenga la columna.");
                        }
                        Check(name != "impresora_pc", "Réplica: la impresora de cada PC no se replica.");
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

    // FD03: RF-13 (filtro de recibos por estado de envío y fechas), RF-21 (boletas no enviadas y reintento), RF-14 (producto.tipo y su
    // migración) y RNF-09 / RN-14 (retiro del consentimiento, importación antigua sin consentimiento). Base temporal propia que se borra al final.
    private static void CheckComprobantesTipoConsentimientoDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDENPOS_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SQL omitido: configure EDENPOS_TEST_MYSQL_CONNECTION para incluir la prueba de comprobantes, tipo y consentimiento.");
            return;
        }
        // Reglas puras de RF-14.
        Check(ProductTypes.Infer("ALQUILERES", 0m) == ProductTypes.Implemento && ProductTypes.Infer("bebidas", 2m) == ProductTypes.Implemento &&
              ProductTypes.Infer("BEBIDAS", 0m) == ProductTypes.Producto, "RF-14: la regla anterior (ALQUILERES o cargo > 0) deduce Implemento.");
        Check(ProductTypes.Normalize("implemento", "BEBIDAS", 0m) == ProductTypes.Implemento && ProductTypes.Normalize("Producto", "ALQUILERES", 5m) == ProductTypes.Producto &&
              ProductTypes.Normalize(null, "ALQUILERES", 0m) == ProductTypes.Implemento && ProductTypes.Normalize("otro", "X", 0m) == ProductTypes.Producto,
            "RF-14: el valor guardado manda; sin valor se deduce.");

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
                    // Base anterior a la migración 009: sin las tres columnas y con artículos que ya eran implementos por la regla anterior.
                    Execute(connection, "ALTER TABLE producto DROP COLUMN tipo");
                    Execute(connection, "ALTER TABLE cliente DROP COLUMN consentimiento_revocado");
                    Execute(connection, "ALTER TABLE comprobante DROP COLUMN mensaje_respuesta");
                    Execute(connection, "INSERT INTO producto (id_producto, categoria, nombre, precio, color_hex, cargo_perdida) VALUES " +
                        "('toalla', 'ALQUILERES', 'TOALLA', 5, '#808080', 0), ('sandalia', 'VARIOS', 'SANDALIA', 3, '#808080', 8), " +
                        "('agua', 'BEBIDAS', 'AGUA', 2, '#808080', 0)");
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
                var columnsSql = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND " +
                                 "((TABLE_NAME = 'producto' AND COLUMN_NAME = 'tipo') OR (TABLE_NAME = 'cliente' AND COLUMN_NAME = 'consentimiento_revocado') OR " +
                                 "(TABLE_NAME = 'comprobante' AND COLUMN_NAME = 'mensaje_respuesta'))";

                // ---- RF-14: migración automática ----
                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3, "009: la migración agrega producto.tipo, cliente.consentimiento_revocado y comprobante.mensaje_respuesta.");
                Func<string, string> tipoOf = id => Convert.ToString(scalar("SELECT tipo FROM producto WHERE id_producto = '" + id + "'"));
                Check(tipoOf("toalla") == "Implemento" && tipoOf("sandalia") == "Implemento" && tipoOf("agua") == "Producto",
                    "RF-14: la migración marca como Implemento ALQUILERES y los artículos con cargo por pérdida.");
                scalar("UPDATE producto SET tipo = 'Producto' WHERE id_producto = 'toalla'");
                AppData.Initialize();
                Check(Convert.ToInt32(scalar(columnsSql)) == 3 && tipoOf("toalla") == "Producto",
                    "RF-14: la migración es idempotente y no vuelve a cambiar lo que eligió la dueña.");
                scalar("UPDATE producto SET tipo = 'Implemento' WHERE id_producto = 'toalla'");

                var owner = AppData.Users.First(u => u.Role == UserRoles.Owner && u.Active);
                var login = AppData.TryLogin(owner.Id, "0000");
                Check(login.Outcome == AppData.LoginOutcome.Success, "FD03: debe iniciar sesión la dueña de la base de prueba.");
                AppSession.SignIn(login.User);

                var products = AppData.Products;
                Check(products.First(p => p.Id == "toalla").IsImplement && AppData.IsImplement(products.First(p => p.Id == "sandalia")) &&
                      !AppData.IsImplement(products.First(p => p.Id == "agua")), "RF-14: el diálogo de devolución solo ofrece artículos de tipo Implemento.");
                // El cargo por pérdida solo vale para implementos (RN-07).
                products.First(p => p.Id == "agua").CargoPerdida = 4m;
                var rejected = false;
                try { AppData.SaveProducts(products); } catch (ArgumentException ex) { rejected = ex.Message.Contains("implementos"); }
                Check(rejected && Convert.ToDecimal(scalar("SELECT cargo_perdida FROM producto WHERE id_producto = 'agua'")) == 0m,
                    "RF-14: un Producto no puede tener cargo por pérdida.");
                products = AppData.Products;
                var agua = products.First(p => p.Id == "agua");
                agua.Tipo = ProductTypes.Implemento; agua.CargoPerdida = 4m;
                AppData.SaveProducts(products);
                Check(tipoOf("agua") == "Implemento" && Convert.ToDecimal(scalar("SELECT cargo_perdida FROM producto WHERE id_producto = 'agua'")) == 4m,
                    "RF-14: el tipo se guarda desde el editor junto con su cargo.");
                products = AppData.Products;
                agua = products.First(p => p.Id == "agua");
                agua.Tipo = ProductTypes.Producto; agua.CargoPerdida = 0m;
                AppData.SaveProducts(products);
                Check(tipoOf("agua") == "Producto" && !AppData.Products.First(p => p.Id == "agua").IsImplement, "RF-14: un implemento puede volver a ser Producto.");

                // ---- RF-21: boletas no enviadas, reintento de una y de todas ----
                if (AppData.OpenShift == null) AppData.OpenNewShift(0m);
                AppData.SimularRechazoComprobantes = true;
                Func<decimal, string, Receipt> sell = (amount, dni) => AppData.AddReceipt(
                    new List<ReceiptLine> { new ReceiptLine { Name = "Servicio de prueba", UnitPrice = amount, Quantity = 1 } }, "ANA PEREZ", dni, "Efectivo", amount);
                var small = sell(50m, "44444444");
                var big = sell(800m, "44444444");
                var other = sell(30m, "");
                System.Threading.Thread.Sleep(300);   // deja terminar el envío en segundo plano de cada venta
                var retry = AppData.ReintentarComprobantes(true);
                Check(!retry.YaEnCurso && retry.Rechazados == 3 && retry.Aceptados == 0, "RF-21: con el rechazo simulado, el reintento deja las tres boletas rechazadas.");
                var pending = AppData.GetComprobantesNoEnviados();
                Check(pending.Count == 3 && pending.All(c => c.Estado == ComprobanteEstados.Rechazado && c.Intentos >= 1 &&
                      c.CodigoRespuesta == EmisorElectronicoStub.CodigoRechazado && (c.MensajeRespuesta ?? "").Contains("simulado") && c.UltimoIntento.HasValue),
                    "RF-21: la lista trae estado, intentos, último código, mensaje (simulado) y fecha del último intento.");
                var bigPending = pending.First(c => c.ReceiptId == big.Id);
                Check(bigPending.Total == 800m && bigPending.CustomerName == "ANA PEREZ" && bigPending.ReceiptNumber == big.Number,
                    "RF-21: la lista trae número, cliente y total del recibo.");
                AppData.SimularRechazoComprobantes = false;
                Check(AppData.ProcesarComprobante(bigPending.Id, true) == ComprobanteEstados.Aceptado, "RF-21: 'Reintentar este' envía solo esa boleta.");
                pending = AppData.GetComprobantesNoEnviados();
                Check(pending.Count == 2 && pending.All(c => c.ReceiptId != big.Id), "RF-21: la lista se actualiza tras reintentar una boleta.");
                Check(AppData.GetComprobante(big.Id).MensajeRespuesta.Contains("simulada"), "RF-21: el mensaje del último envío se guarda (aceptado de forma simulada).");
                AppData.SimularRechazoComprobantes = true;
                AppData.ReintentarComprobantes(true);
                AppData.SimularRechazoComprobantes = false;
                retry = AppData.ReintentarComprobantes(true);
                Check(retry.Aceptados == 2 && AppData.GetComprobantesNoEnviados().Count == 0, "RF-21: 'Reintentar todos' envía las que quedaban y vacía la lista.");

                // ---- RF-13: filtro por estado de envío y por fechas ----
                var filter = typeof(AppData).Assembly.GetType("EdenPOS.ReceiptsView", true);
                Func<string, DateTime?, DateTime?, List<Receipt>> filtered = (state, from, to) =>
                    ((IEnumerable<Receipt>)Invoke(filter, null, "FilterReceipts", AppData.Receipts, state, from, to)).ToList();
                scalar("UPDATE comprobante SET estado_envio = 'Rechazado' WHERE id_recibo = '" + small.Id + "'");
                scalar("UPDATE comprobante SET estado_envio = 'Pendiente' WHERE id_recibo = '" + other.Id + "'");
                scalar("UPDATE recibo SET created_at = DATE_SUB(created_at, INTERVAL 3 DAY) WHERE id_recibo = '" + other.Id + "'");
                Check(filtered(null, null, null).Count == 3, "RF-13: sin filtros se ven todos los recibos.");
                Check(filtered(ComprobanteEstados.Aceptado, null, null).Select(r => r.Id).SequenceEqual(new[] { big.Id }), "RF-13: filtro por Aceptado.");
                Check(filtered(ComprobanteEstados.Rechazado, null, null).Select(r => r.Id).SequenceEqual(new[] { small.Id }), "RF-13: filtro por Rechazado.");
                Check(filtered(ComprobanteEstados.Pendiente, null, null).Select(r => r.Id).SequenceEqual(new[] { other.Id }), "RF-13: filtro por Pendiente.");
                Check(filtered(null, DateTime.Today, DateTime.Today).Count == 2 && filtered(null, null, DateTime.Today.AddDays(-1)).Select(r => r.Id).SequenceEqual(new[] { other.Id }),
                    "RF-13: filtro por rango de fechas (extremos incluidos).");
                Check(filtered(ComprobanteEstados.Pendiente, DateTime.Today, null).Count == 0, "RF-13: estado y fechas se combinan.");
                scalar("UPDATE comprobante SET estado_envio = 'Aceptado' WHERE id_recibo IN ('" + small.Id + "', '" + other.Id + "')");

                // ---- RNF-09 / RN-14: retiro del consentimiento ----
                AppData.SaveCustomer("44444444", "Ana Pérez", "Prueba", "987654321", true);
                scalar("INSERT INTO ticket_guardado (id_ticket, nombre, cliente_nombre, cliente_dni, adultos, ninos, created_at, updated_at) " +
                       "VALUES ('ticketana', 'Ana', 'ANA PEREZ', '44444444', 1, 0, NOW(), NOW())");
                AppSession.SignOut();
                var denied = false;
                try { AppData.RevokeCustomerConsent("44444444"); } catch (InvalidOperationException) { denied = true; }
                Check(denied && AppData.FindCustomer("44444444") != null, "RNF-09: solo la dueña registra el retiro del consentimiento.");
                AppSession.SignIn(login.User);
                var revoked = AppData.RevokeCustomerConsent("44444444");
                Check(revoked.ReceiptsAnonymized == 1 && revoked.ReceiptsKept == 1 && revoked.TicketsAnonymized == 1,
                    "RNF-09: anonimiza el recibo de S/ 700 o menos y el ticket; conserva el recibo mayor a S/ 700.");
                Check(AppData.FindCustomer("44444444") == null, "RNF-09: el DNI real ya no está en el registro de clientes.");
                var anonymized = AppData.Customers.Single(c => c.ConsentRevokedAt.HasValue);
                Check(AppData.IsAnonymizedDni(anonymized.Dni) && !Validation.IsValidDni(anonymized.Dni) && anonymized.Phone == "" && !anonymized.DataConsent &&
                      anonymized.FullName == "ANA PÉREZ", "RNF-09: queda constancia con fecha de retiro, sin teléfono, sin consentimiento y con DNI anonimizado.");
                Check(scalar("SELECT cliente_dni FROM recibo WHERE id_recibo = '" + small.Id + "'") is DBNull &&
                      Convert.ToString(scalar("SELECT cliente_dni FROM recibo WHERE id_recibo = '" + big.Id + "'")) == "44444444" &&
                      scalar("SELECT cliente_dni FROM ticket_guardado WHERE id_ticket = 'ticketana'") is DBNull,
                    "RNF-09: los recibos mayores a S/ 700 conservan el documento (SUNAT).");
                var detail = Convert.ToString(scalar("SELECT detalle FROM auditoria WHERE accion = 'CONSENTIMIENTO_RETIRADO' ORDER BY id_auditoria DESC LIMIT 1"));
                Check(detail.Contains("ANA PÉREZ") && !detail.Contains("44444444"), "RNF-09: la auditoría registra CONSENTIMIENTO_RETIRADO sin el DNI.");
                var again = false;
                try { AppData.RevokeCustomerConsent(anonymized.Dni); } catch (InvalidOperationException) { again = true; }
                Check(again, "RNF-09: no se puede retirar dos veces.");
                var back = AppData.SaveCustomer("44444444", "Ana Pérez", "Prueba", null, true);
                Check(back.DataConsent && !back.ConsentRevokedAt.HasValue && back.Phone == "", "RNF-09: si vuelve y acepta, se registra de nuevo desde cero.");
                AppData.RegistrarExportacionDatos("Prueba de exportación");
                Check(Convert.ToInt32(scalar("SELECT COUNT(*) FROM auditoria WHERE accion = 'DATOS_EXPORTADOS'")) == 1, "RNF-09: la exportación de datos queda en auditoría.");

                // ---- Copias: la nueva lleva las columnas; una anterior a 009 se restaura con los valores por defecto y el tipo deducido ----
                var current = Path.Combine(Path.GetTempPath(), "EdenPOS-fd03-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                var legacy = Path.Combine(Path.GetTempPath(), "EdenPOS-fd03-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                var legacyJson = Path.Combine(Path.GetTempPath(), "EdenPOS-fd03-" + Guid.NewGuid().ToString("N") + ".edenbackup");
                try
                {
                    scalar("UPDATE producto SET tipo = 'Producto' WHERE id_producto = 'toalla'");   // decisión de la dueña que la copia nueva debe conservar
                    AppData.ExportBackup(current);
                    File.Copy(current, legacy);
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    using (var zip = System.IO.Compression.ZipFile.Open(legacy, System.IO.Compression.ZipArchiveMode.Update))
                    {
                        foreach (var pair in new[] { new[] { "producto", "tipo" }, new[] { "cliente", "consentimiento_revocado" }, new[] { "comprobante", "mensaje_respuesta" } })
                        {
                            var entry = zip.GetEntry("tables/" + pair[0] + ".json");
                            string json;
                            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
                            var rows = serializer.Deserialize<List<Dictionary<string, object>>>(json);
                            Check(rows.Count > 0 && rows.All(r => r.ContainsKey(pair[1])), "009: la copia de seguridad incluye " + pair[0] + "." + pair[1] + ".");
                            foreach (var row in rows) row.Remove(pair[1]);
                            entry.Delete();
                            using (var writer = new StreamWriter(zip.CreateEntry("tables/" + pair[0] + ".json").Open(), new UTF8Encoding(false))) writer.Write(serializer.Serialize(rows));
                        }
                    }
                    AppData.ImportBackup(legacy);
                    Check(tipoOf("toalla") == "Implemento" && tipoOf("sandalia") == "Implemento" && tipoOf("agua") == "Producto" &&
                          Convert.ToInt32(scalar("SELECT COUNT(*) FROM cliente WHERE consentimiento_revocado IS NOT NULL")) == 0 &&
                          Convert.ToInt32(scalar("SELECT COUNT(*) FROM comprobante WHERE mensaje_respuesta IS NOT NULL")) == 0,
                        "009: una copia anterior se restaura con valores por defecto y el tipo deducido por la regla anterior.");
                    AppData.ImportBackup(current);
                    Check(tipoOf("toalla") == "Producto" && Convert.ToInt32(scalar("SELECT COUNT(*) FROM cliente WHERE consentimiento_revocado IS NOT NULL")) == 1 &&
                          Convert.ToInt32(scalar("SELECT COUNT(*) FROM comprobante WHERE mensaje_respuesta IS NOT NULL")) == 3,
                        "009: una copia nueva conserva tipo, fecha de retiro y mensajes.");

                    // ---- Importación de datos antiguos (JSON): clientes sin consentimiento no se importan ----
                    using (var zip = System.IO.Compression.ZipFile.Open(legacyJson, System.IO.Compression.ZipArchiveMode.Create))
                    {
                        Action<string, string> add = (name, text) =>
                        {
                            using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(text);
                        };
                        add("settings.json", "{\"StoreName\":\"EDEN PRUEBA\",\"CurrencySymbol\":\"S/\",\"NextReceiptNumber\":1,\"NextShiftNumber\":1,\"Users\":[]}");
                        add("catalog.json", "[{\"Id\":\"alq1\",\"Name\":\"TOALLA\",\"Category\":\"ALQUILERES\",\"Price\":5,\"ColorHex\":\"#808080\",\"Active\":true}," +
                                            "{\"Id\":\"beb1\",\"Name\":\"AGUA\",\"Category\":\"BEBIDAS\",\"Price\":2,\"ColorHex\":\"#808080\",\"Active\":true}]");
                        add("customers.json", "[{\"Dni\":\"55555555\",\"FullName\":\"SIN PERMISO\",\"Source\":\"Antiguo\",\"Phone\":\"911111111\",\"CreatedAt\":\"2025-01-01T10:00:00\",\"UpdatedAt\":\"2025-01-01T10:00:00\"}," +
                                               "{\"Dni\":\"66666666\",\"FullName\":\"CON PERMISO\",\"Source\":\"Antiguo\",\"Phone\":\"922222222\",\"DataConsent\":true,\"CreatedAt\":\"2025-01-01T10:00:00\",\"UpdatedAt\":\"2025-01-02T10:00:00\"}]");
                    }
                    AppData.ImportBackup(legacyJson);
                    var imported = AppData.Customers;
                    Check(imported.Count == 1 && imported[0].Dni == "66666666" && imported[0].DataConsent && imported[0].Phone == "922222222" && imported[0].ConsentDate.HasValue,
                        "RNF-09: la importación antigua solo guarda el DNI y el teléfono de clientes con consentimiento.");
                    Check(tipoOf("alq1") == "Implemento" && tipoOf("beb1") == "Producto", "RF-14: la importación antigua deduce el tipo (ALQUILERES = Implemento).");
                }
                finally
                {
                    foreach (var path in new[] { current, legacy, legacyJson }) if (File.Exists(path)) File.Delete(path);
                    AppSession.SignOut();
                }
                Console.WriteLine("SQL: RF-13, RF-21, RF-14 y RNF-09 (FD03) en una base temporal aislada.");
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
                AppData.CloseShift(shift.Id, 140m, AppData.CalculateExpectedYape(shift), "Cierre de prueba");
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

                // RNF-09 + RN-01: al retirar el consentimiento, un recibo con saldo pendiente sin cobrar conserva el DNI (deuda viva); los demás lo pierden.
                AppData.SaveCustomer("55555555", "Rosa Deuda", "Prueba", null, true);
                var debt = AppData.AddReceipt(saunaLines(), "Rosa Deuda", "55555555", "Efectivo", 0m, pendingBalance: auth(total, owner.Id, "0000"));
                var paidVisit = AppData.AddReceipt(saunaLines(), "Rosa Deuda", "55555555", "Yape", 0m);
                var revokedDebt = AppData.RevokeCustomerConsent("55555555");
                Check(Convert.ToString(scalar("SELECT cliente_dni FROM recibo WHERE id_recibo = '" + debt.Id + "'")) == "55555555" &&
                      scalar("SELECT cliente_dni FROM recibo WHERE id_recibo = '" + paidVisit.Id + "'") is DBNull && revokedDebt.ReceiptsKept == 1,
                    "RNF-09: el retiro del consentimiento conserva el DNI solo en el recibo con saldo pendiente.");

                // RN-01 + RF-05: el saldo cobrado cuenta en el turno donde se cobró (saldo_id_sesion), no por fechas.
                var debtShift = AppData.OpenShift;
                var before = AppData.GetShiftBalance(debtShift);
                AppData.CollectBalance(debt.Id, "Yape", 0m);
                var after = AppData.GetShiftBalance(debtShift);
                Check(after.YapeSales - before.YapeSales == total && after.CashSales == before.CashSales, "RF-05: el saldo cobrado por Yape suma al Yape esperado de su turno.");

                // Réplica en Supabase: las columnas nuevas de recibo se replican (si el esquema remoto las tiene).
                var tables = (System.Collections.IEnumerable)Invoke(typeof(AppData), null, "ReplicaBuildTables");
                foreach (var info in tables)
                {
                    var table = Member(info, "Table");
                    if ((string)Member(table, "Name") != "recibo") continue;
                    var columns = (string[])Member(table, "Columns");
                    Check(columns.Contains("total") && newColumns.All(c => columns.Contains(c)), "RN-01: la réplica incluye las columnas nuevas de recibo cuando Supabase las admite.");
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
                        Check(rows.Count == 10 && rows.All(r => newColumns.All(r.ContainsKey)), "RN-01: la copia de seguridad incluye las columnas nuevas de recibo.");
                        foreach (var row in rows) foreach (var column in newColumns) row.Remove(column);
                        entry.Delete();
                        using (var writer = new StreamWriter(zip.CreateEntry("tables/recibo.json").Open(), new UTF8Encoding(false))) writer.Write(serializer.Serialize(rows));
                    }
                    AppData.ImportBackup(legacy);
                    var restoredLegacy = AppData.Receipts;
                    Check(restoredLegacy.Count == 10 && restoredLegacy.All(r => r.PendingBalance == 0m && r.CheckInAt == null && r.BalanceCollectedAt == null),
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
