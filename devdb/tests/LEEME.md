# Comprobaciones de regresión

Ejecutar en Windows con el SDK de .NET y .NET Framework 4.8:

```powershell
dotnet build devdb\tests\RegressionChecks.csproj -c Release
.\devdb\tests\bin\Release\net48\RegressionChecks.exe
```

Verifican escritura atómica, recuperación ante archivos bloqueados, limpieza de
temporales y conservación de claves de configuración. Usan una carpeta temporal
propia y no modifican los ajustes de la aplicación instalada.

Para comprobar también los contadores de Inventario contra MySQL/MariaDB:

```powershell
$env:EDENPOS_TEST_MYSQL_CONNECTION = 'Server=127.0.0.1;Port=3307;User ID=root;Password=;'
.\devdb\tests\bin\Release\net48\RegressionChecks.exe
Remove-Item Env:\EDENPOS_TEST_MYSQL_CONNECTION
```

Use un servidor de desarrollo. La prueba crea una base `edenpos_test_<id>` propia
y la elimina al terminar; el usuario SQL necesita permisos para crear y eliminar
bases. Compara los resultados con el cálculo anterior usando ventas antiguas,
medianoche, reembolsos, variantes por identificador y nombres de recibos antiguos.
La prueba SQL se omite explícitamente si no se configura la variable.
