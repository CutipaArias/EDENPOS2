# Comprobaciones de regresión

Ejecutar en Windows con el SDK de .NET y .NET Framework 4.8:

```powershell
dotnet build devdb\tests\RegressionChecks.csproj -c Release
.\devdb\tests\bin\Release\net48\RegressionChecks.exe
```

Verifican escritura atómica, recuperación ante archivos bloqueados, limpieza de
temporales y conservación de claves de configuración. Usan una carpeta temporal
propia y no modifican los ajustes de la aplicación instalada.

También comprueban el registro concurrente de errores, la rotación del archivo
al alcanzar 2 MiB y la recuperación después de un bloqueo. Se conserva el bloque
anterior en `errors.log.1`; no se recorta el contenido de una excepción individual.

Las comprobaciones de DNI cubren nombres completos y separados, respuestas
dañadas, tipos JSON inválidos y consultas concurrentes. No contactan al proveedor.

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
Las pruebas de seguridad rechazan hashes y sales de PIN truncados, configuraciones
locales dañadas y credenciales que no se pueden descifrar. Verifican que estas
configuraciones nunca provoquen una conexión alternativa con el usuario predeterminado.
Las pruebas SQL también fuerzan la reutilización de una única conexión del pool,
comprueban el restablecimiento de claves foráneas, modo estricto y variables de
sesión, y verifican el rechazo de comandos sin su transacción y la reversión de
escrituras no confirmadas.
También verifica que la consulta de DNI conserve el registro local y devuelva
el control de inmediato cuando el acceso a datos esté ocupado.
La prueba de caja (RN-02, RF-04, RF-05, RNF-04) parte de una base sin las columnas de
`007_caja_medios.sql`, comprueba la migración, el rechazo de cobros sin turno (también
cuando otra caja cierra el turno a la vez), el esperado por medio de pago, la observación
obligatoria, la auditoría de cada cierre, el reporte de caja y copias nuevas y antiguas.
