# Comprobaciones de regresión

Ejecutar en Windows con el SDK de .NET y .NET Framework 4.8:

```powershell
dotnet build devdb\tests\RegressionChecks.csproj -c Release
.\devdb\tests\bin\Release\net48\RegressionChecks.exe
```

Verifican escritura atómica, recuperación ante archivos bloqueados, limpieza de
temporales y conservación de claves de configuración. Usan una carpeta temporal
propia y no modifican los ajustes de la aplicación instalada.
