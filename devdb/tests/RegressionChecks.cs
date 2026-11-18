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
            CheckInventoryDatabase();
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
                Environment.SetEnvironmentVariable("EDENPOS_MYSQL_CONNECTION", builder.ConnectionString);
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
