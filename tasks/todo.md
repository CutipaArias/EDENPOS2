# To-do: Completar EdenPOS

Ver `tasks/plan.md` para contexto completo de cada tarea. Verificación en todas, desde la raíz del repositorio:
1. `dotnet build EdenPOS.csproj -c Release`.
2. Pruebas de regresión: `dotnet build devdb\tests\RegressionChecks.csproj -c Release`, luego `.\devdb\tests\bin\Release\net48\RegressionChecks.exe` con `EDENPOS_TEST_MYSQL_CONNECTION` definida. Deben terminar con "Correcto: 309 comprobaciones."
3. El paso manual descrito en cada tarea.

## Phase 0: Preparación
- [x] 0.1 Instalar MySQL 8.4.9 local y crear base `edenpos` con el esquema (puerto 3306, dev-only, sin contraseña de root)

## Phase 1: sql-migration
- [x] 1.1 `MySqlConnector` + `SqlConnectionFactory` + cadena de conexión en `AppSettings` (compila; sin probar en la UI)
- [x] 1.2 Migrar Productos/Variantes/Inventario a SQL (compila y revisado; sin probar en la UI)
- [x] 1.3 Migrar Clientes a SQL (compila; sin probar en la UI)
- [x] 1.4 Migrar Caja (sesiones + movimientos) a SQL (compila; sin probar en la UI)
- [x] 1.5 Migrar Recibos (venta + reembolso, transaccional) a SQL (compila; sin probar en la UI)
- [x] 1.6 Migrar Tickets guardados + llaves a SQL (compila; sin probar en la UI)
- [x] 1.7 Migrar Usuarios/Roles/Configuración a SQL (compila; sin probar en la UI)
- [x] 1.8 Herramienta de migración única desde JSON existente (hecho: importación probada con una copia de los datos reales, 985 filas; UI sin probar)
- [x] 1.9 Rediseñar copia de seguridad (exportar/restaurar) para datos en MySQL — hoy ya no cubre productos ni inventario y restaurar una copia nueva falla (hecho: copia v2 sobre MySQL; compila, sin probar en la UI)

### Checkpoint 1
- [x] Build limpio (2026-11-25)
- [x] Orden de variantes y desempate de movimientos del mismo segundo (resueltos en el código)
- [ ] Las 7 pantallas existentes funcionan 100% contra MySQL (prueba manual)
- [ ] Antes de usar la app en el sauna: usuario de MySQL propio en lugar de `root` sin contraseña, y cambiar el PIN `0000` de DUEÑA
- [ ] Revisado con el usuario

## Phase 2: funcionalidad documentada faltante
- [x] 2.1 Promociones (sexta visita gratis + descuento grupal/familiar) (probado por reflexión; UI sin probar)
- [x] 2.2 Facturación electrónica (stub) + regla DNI>S/700 (probado por reflexión; UI sin probar)
- [x] 2.3 Auditoría (tabla + registro en operaciones sensibles) (probado por reflexión; UI sin probar)
- [x] 2.4 Stock mínimo por producto (probado por reflexión; UI sin probar)

### Checkpoint 2
- [x] Build limpio (2026-11-25)
- [ ] Los 4 flujos verificados manualmente
- [ ] Revisado con el usuario

## Phase 3: reports
- [x] 3.1 Reportes — Caja (probado por reflexión; UI sin probar)
- [x] 3.2 Reportes — Ventas (probado por reflexión; UI sin probar)
- [x] 3.3 Reportes — Inventario (probado por reflexión; UI sin probar)
- [x] 3.4 Pantalla de Auditoría (independiente) (probado por reflexión; UI sin probar)

### Checkpoint 3
- [x] Build limpio (2026-11-25)
- [ ] Reportes y auditoría muestran datos correctos (prueba manual)

## Phase 4: design-pass (paralelo)
- [x] 4.1 Escala tipográfica en Theme.cs (escala definida; aún sin usar en pantallas)
- [x] 4.2 Feedback de botón al presionar (MouseDownBackColor)
- [x] 4.3 Auditoría de consistencia de color/espaciado en pantallas existentes (pantallas nuevas con tokens de Theme; las existentes no se tocaron para no descuadrar coordenadas fijas)
- [x] 4.4 Aplicar estándar a pantallas nuevas de Fases 2–3

## Estado final (2026-11-16)
- Fases 1–4 implementadas, revisadas y probadas por reflexión: 98/98 comprobaciones contra el MariaDB 10.4 de XAMPP (puerto 3306, base temporal ya borrada).
- Pendiente: prueba manual del usuario en la interfaz (checkpoints).

## Rediseño visual (2026-11-17)
- [x] Sistema de diseño en `Theme.cs` + `Controls.cs` (tokens, botones, campos, tablas, tarjetas, cabecera, etiquetas, avisos)
- [x] Herramienta de capturas `devdb/tools/Capturas.ps1` y comparativa `devdb/capturas/comparativa.html`
- [x] Rediseño de todas las pantallas y diálogos (inicio, menú, ventas y cobro, inventario, turno, recibos, configuración, usuarios, reportes, auditoría)
- [x] Textos unificados a "usted", con tildes y mayúscula inicial
- [x] Revisión de regresiones y correcciones (sin consultas a MySQL por cada toque en Ventas; vistas liberadas al cerrar sesión)
- [ ] Prueba manual del usuario en la app (incluida escala de Windows 125–150 %)

## Lo bueno del proyecto con Supabase, sobre MariaDB (2026-11-18)
- [x] Cobro de ticket guardado y liberación de candados en una sola transacción (error claro si otra caja ya lo cobró)
- [x] Huella optimista del catálogo; guardar Inventario ya no pisa el stock de artículos existentes
- [x] Refresco automático de la vista activa cuando otra caja cambia datos (sondeo cada 5 s en segundo plano; sin Configuración, Reportes ni Auditoría)
- [x] Copia automática a una carpeta elegida (OneDrive/Google Drive): al cerrar turno y cada 24 h, retención por equipo
- [x] Revisión y correcciones: exportación sin bloquear ventas, settings.json no se pierde si está ocupado, orden de bloqueos de tickets
- [ ] Prueba manual con dos cajas contra la misma base y con la carpeta de copias real

## Réplica en Supabase (2026-11-18)
- [x] Npgsql 8.0.9 + `SupabaseTarget.cs` (TLS con CA propia, upsert por lotes, errores en español sin contraseña)
- [x] `SupabaseReplica.cs`: sube solo filas cambiadas cada 5 min o tras cambios; espera creciente sin internet; freno ante borrados masivos; restaurar con vista previa y copia previa
- [x] Configuración → Réplica en Supabase; scripts `devdb/supabase/01_esquema_replica.sql`, `02_vaciar_replica.sql` y `LEEME.md`
- [x] Revisión y correcciones (probado con destino simulado y MySQL 3307)
- [ ] Prueba del usuario con su proyecto Supabase real (script, conexión, primera subida, restaurar en base limpia, API pública sin acceso)

## Estado (2026-11-25)
- Código completo: sin issues ni pull requests abiertos en GitHub; build limpio y "Correcto: 309 comprobaciones." con el MariaDB 10.4.32 de XAMPP.
- Lo que falta son las casillas sin marcar de arriba: pruebas manuales en la interfaz (checkpoints, escala de Windows 125-150 %, dos cajas, copia en una carpeta real, Supabase real) y la preparación para usar la app en el sauna.
