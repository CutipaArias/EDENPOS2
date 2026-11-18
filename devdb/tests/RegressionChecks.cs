using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using EdenPOS;

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
}
