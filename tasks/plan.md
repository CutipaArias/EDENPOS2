# Implementation Plan: Completar EdenPOS (SQL + features + diseño)

## Overview

Cerrar las brechas de EdenPOS contra FD03/FD04 (ver `SPEC.md`): migrar el almacenamiento de JSON a MySQL, agregar promociones, facturación electrónica (stub), auditoría, stock mínimo y reportes, más un refresco visual. MySQL 8.4.9 ya está instalado y corriendo localmente (puerto 3306, sin contraseña de root — solo para desarrollo) con la base `edenpos` y sus 16 tablas creadas desde el esquema del SPEC.

## Architecture Decisions

- Acceso a datos vía `MySqlConnector` (NuGet) + ADO.NET plano, sin EF Core (justificado en SPEC.md).
- `AppData` conserva su API pública; solo cambia la implementación interna de cada método.
- Migración de JSON existente a SQL como paso explícito y reversible (JSON no se borra hasta confirmar).
- Promociones calculadas sobre el historial de `Receipt` por DNI, sin entidad `Visita` nueva.
- Facturación electrónica: adaptador stub, reintento con `System.Threading.Timer` cada 5 min mientras la app está abierta.

## Task List

### Phase 0: Preparación (ya hecho)
- [x] Task 0.1: Instalar MySQL 8.4.9 localmente y crear base `edenpos` con el esquema del SPEC.

### Phase 1: `sql-migration`

- [x] Task 1.1: Agregar dependencia `MySqlConnector` al proyecto y una clase `SqlConnectionFactory` que lee la cadena de conexión desde `AppSettings` (nuevo campo `MySqlConnectionString`, protegido con DPAPI igual que el token de DNI).
  - Acceptance: el proyecto compila con la referencia nueva; `SqlConnectionFactory.Open()` devuelve una `MySqlConnection` abierta contra `edenpos`.
  - Verify: `dotnet build`; prueba manual ejecutando una consulta `SELECT 1` desde una pantalla de diagnóstico temporal o el depurador.
  - Files: `EdenPOS.csproj`, `Infrastructure.cs` (nueva clase), `Models.cs` (campo nuevo en `AppSettings`).
  - Scope: S

- [x] Task 1.2: Migrar `Products`/`SaveProducts`/`AddInventoryEntry`/`DeleteProduct` de JSON a tablas `producto`/`producto_variante`/`movimiento_inventario`.
  - Acceptance: `ProductsView` lee y escribe productos contra MySQL; el checkbox "Controla inventario" y el fix de inventario siguen funcionando.
  - Verify: `dotnet build`; crear/editar/eliminar un producto desde la UI y confirmar los cambios con una consulta SQL directa.
  - Files: `Infrastructure.cs`, (sin cambios en `ProductsView.cs` si la firma de `AppData` no cambia).
  - Dependencies: 1.1
  - Scope: M

- [x] Task 1.3: Migrar `Customers`/`FindCustomer`/`SaveCustomer` a tabla `cliente`.
  - Acceptance: registrar/buscar cliente por DNI funciona contra MySQL.
  - Verify: `dotnet build`; registrar un cliente desde `SalesView` y confirmarlo por SQL.
  - Files: `Infrastructure.cs`.
  - Dependencies: 1.1
  - Scope: S

- [x] Task 1.4: Migrar `Shifts`/`OpenNewShift`/`CloseShift`/`CashMovements`/`AddCashMovement` a `sesion_caja`/`movimiento_caja`.
  - Acceptance: abrir/cerrar caja y registrar movimientos funciona contra MySQL, incluyendo el cálculo de `CalculateExpectedCash`.
  - Verify: `dotnet build`; abrir caja, registrar un movimiento, cerrarla, comparar el esperado contra lo calculado manualmente.
  - Files: `Infrastructure.cs`.
  - Dependencies: 1.1
  - Scope: M

- [x] Task 1.5: Migrar `Receipts`/`AddReceipt`/`RefundReceipt` a `recibo`/`recibo_linea`, con transacción (recibo + líneas + descuento de stock en una sola transacción SQL).
  - Acceptance: una venta completa (con y sin stock, con y sin reembolso) queda correctamente reflejada en `recibo`, `recibo_linea` y `movimiento_inventario`.
  - Verify: `dotnet build`; vender, reembolsar, confirmar por SQL que el stock y los montos cuadran.
  - Files: `Infrastructure.cs`.
  - Dependencies: 1.2, 1.4
  - Scope: M

- [x] Task 1.6: Migrar `SavedTickets`/`SaveTicket`/`UpdateSavedTicket`/`DeleteSavedTicket`/locker tracking a `ticket_guardado`/`ticket_linea`/`casillero_llave`.
  - Acceptance: guardar/actualizar/eliminar tickets y la validación de llaves duplicadas sigue funcionando contra MySQL.
  - Verify: `dotnet build`; guardar un ticket con 2 llaves, intentar asignar una de esas llaves a otro ticket y confirmar el rechazo.
  - Files: `Infrastructure.cs`.
  - Dependencies: 1.1
  - Scope: M

- [x] Task 1.7: Migrar `Users`/roles/`Settings` a `usuario`/`rol`/`configuracion`.
  - Acceptance: login, gestión de usuarios y configuración funcionan contra MySQL.
  - Verify: `dotnet build`; iniciar sesión, crear un usuario, cambiar un ajuste y confirmarlo por SQL.
  - Files: `Infrastructure.cs`, `Security.cs`.
  - Dependencies: 1.1
  - Scope: M

- [x] Task 1.8: Herramienta de migración única `--migrate-json` que lee los JSON existentes de `DataDirectory` y los inserta en MySQL (sin borrar los JSON).
  - Acceptance: corriendo la app con ese flag una vez, los datos JSON existentes aparecen en MySQL.
  - Verify: `dotnet build`; correr contra una copia de los JSON reales del usuario y comparar conteos de filas.
  - Files: `Program.cs`, `Infrastructure.cs`.
  - Dependencies: 1.2–1.7
  - Scope: M

- [x] Task 1.9: Rediseñar `ExportBackup`/`ImportBackup` para que la copia incluya los datos que viven en MySQL (añadida tras la revisión de 1.2: la copia actual solo comprime archivos JSON, ya no cubre productos ni inventario, e `ImportBackup` exige un `catalog.json` que ya no se genera).
  - Acceptance: exportar y restaurar una copia en una instalación nueva recupera productos, stock, recibos, caja, clientes y usuarios; la cadena de conexión y el token de DNI no viajan en la copia.
  - Verify: `dotnet build`; exportar, cambiar un dato, restaurar y confirmar por SQL que volvió al valor anterior.
  - Files: `Infrastructure.cs`.
  - Dependencies: 1.2–1.7
  - Scope: M

Pendientes que quedaron para el Checkpoint 1 (estado al 2026-11-25):
- [x] Orden de las variantes: columna `producto_variante.orden`; se devuelven en el orden en que se crearon.
- [x] Desempate de movimientos del mismo segundo: la caja ordena por `created_at, id_movimiento` y el inventario por `movimiento_inventario.secuencia`.
- [ ] Cadena de conexión por defecto `root` sin contraseña: se conserva para el primer inicio con XAMPP (ver README → Conexión y seguridad). Antes de usar la app en el sauna, crear un usuario de MySQL con permisos solo sobre `edenpos` y configurarlo en `EDENPOS_MYSQL_CONNECTION`.

### Checkpoint: Fin de `sql-migration`
- [x] `dotnet build` limpio (verificado el 2026-11-25).
- [ ] Las 7 pantallas existentes (Login, Ventas, Productos, Caja, Recibos, Usuarios, Configuración) operan end-to-end contra MySQL sin tocar JSON.
- [ ] Revisar con el usuario antes de continuar.

### Phase 2: Módulos funcionales (paralelizables entre sí, no con Fase 1)

- [x] Task 2.1 (`promotions`): clases `Promocion`/`SextaVisitaGratis`/`DescuentoGrupal` + `PromocionCalculadora`, configuración en `configuracion`, UI en `CheckoutDialog`.
  - Acceptance: la 6ª visita paga de un DNI exonera la línea de categoría SAUNA de mayor precio; un grupo ≥ mínimo configurado recibe el descuento grupal; nunca se acumulan ambas.
  - Verify: `dotnet build`; simular 6 ventas del mismo DNI y confirmar la exoneración en la 6ª; vender con 4+ personas y confirmar el descuento.
  - Files: nuevo `Promotions.cs`, `Dialogs.cs`, `SettingsView.cs`, `Infrastructure.cs`.
  - Dependencies: Phase 1 completo
  - Scope: M

- [x] Task 2.2 (`e-invoicing`): `IEmisorElectronico`, `EmisorElectronicoStub`, tabla `comprobante`, validación RN-11 (DNI si total > S/700), reintento con `Timer`.
  - Acceptance: cada venta genera un comprobante `Pendiente` que el stub resuelve; una venta > S/700 sin DNI es rechazada; un comprobante `Rechazado` se reintenta automáticamente.
  - Verify: `dotnet build`; vender > S/700 sin DNI (debe bloquear), configurar el stub para rechazar y confirmar que el reintento corre tras 5 min (o un valor reducido para la prueba).
  - Files: nuevo `ElectronicInvoicing.cs`, `Infrastructure.cs`, `Dialogs.cs`.
  - Dependencies: Phase 1 completo
  - Scope: M

- [x] Task 2.3 (`audit-trail`): tabla `auditoria`, `AppData.RegistrarAuditoria`, llamadas desde `DeleteProduct`, `RefundReceipt`, `CloseShift` (con diferencia), `DeleteUser`, `SetUserActive(false)`.
  - Acceptance: cada una de esas operaciones deja una fila en `auditoria` con usuario, acción y detalle.
  - Verify: `dotnet build`; ejecutar cada operación y confirmar la fila por SQL.
  - Files: `Infrastructure.cs`.
  - Dependencies: Phase 1 completo
  - Scope: S

- [x] Task 2.4 (`stock-minimo`): campo `StockMinimo` en `Product`/`ProductOption`, UI en `ProductEditorDialog`, usar el campo en vez del `<= 3` fijo en `ProductsView`.
  - Acceptance: el umbral de alerta de stock bajo es configurable por producto, no fijo.
  - Verify: `dotnet build`; poner stock mínimo en 10 para un producto con stock 8 y confirmar que se resalta.
  - Files: `Models.cs`, `ProductsView.cs`.
  - Dependencies: Phase 1 completo
  - Scope: S

### Checkpoint: Fin de Fase 2
- [x] `dotnet build` limpio (verificado el 2026-11-25).
- [ ] Los 4 flujos de aceptación anteriores verificados manualmente.
- [ ] Revisar con el usuario antes de continuar a Reportes.

### Phase 3: `reports`

- [x] Task 3.1: `ReportsView` con selector de rango de fechas y sección de Caja (turnos, apertura/cierre/diferencia).
  - Dependencies: Phase 1, Phase 2 (necesita `sesion_caja`)
  - Scope: S
- [x] Task 3.2: Sección de Ventas (total, por medio de pago, por categoría, promociones aplicadas).
  - Dependencies: 3.1, Task 2.1
  - Scope: S
- [x] Task 3.3: Sección de Inventario (movimientos por producto, productos en stock mínimo).
  - Dependencies: 3.1, Task 2.4
  - Scope: S
- [x] Task 3.4: Pantalla nueva de Auditoría (grid filtrable por fecha/usuario) — decidido como pantalla independiente, no pestaña de Configuración.
  - Dependencies: Task 2.3
  - Scope: S

### Checkpoint: Fin de Fase 3
- [x] `dotnet build` limpio (verificado el 2026-11-25).
- [ ] Reportes y Auditoría muestran datos reales y correctos para un rango de prueba.

### Phase 4: `design-pass` (en paralelo a cualquier fase)

- [x] Task 4.1: Escala tipográfica en `Theme.cs` (Display/Title/Heading/Body/Caption: tamaño+peso+espaciado como conjunto).
- [x] Task 4.2: `FlatAppearance.MouseDownBackColor` en `Theme.Button` (feedback instantáneo al presionar, no al soltar).
- [x] Task 4.3: Auditoría de consistencia de color/espaciado entre pantallas existentes usando los tokens de `Theme` en vez de valores sueltos.
- [x] Task 4.4: Aplicar la escala tipográfica y el estándar de etiquetas directas a las pantallas nuevas (Reportes, Auditoría, Promociones en Configuración) a medida que se construyen en Fases 2–3.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| MySQL de desarrollo corre sin contraseña de root y no como servicio de Windows | Alto si se confunde con producción | Documentar claramente que este MySQL es solo para desarrollo en esta sesión; antes de desplegar en el sauna, instalar como servicio con contraseña real (paso de despliegue separado, no de este plan) |
| Migración de JSON a SQL pierde datos si hay un bug | Alto (datos reales del sauna) | Task 1.8 nunca borra los JSON; se valida por conteo de filas antes de considerar la migración terminada |
| Regresiones silenciosas | Medio | `devdb/tests/RegressionChecks.csproj` (309 comprobaciones contra bases temporales, incluidas las mediciones de RNF-01 y RNF-05); además, cada tarea tiene un paso de verificación manual y hay checkpoints cada 2-3 tareas |
| Promociones mal definidas respecto a lo que el negocio realmente quiere | Medio | Confirmado con el usuario qué categoría de producto es "SAUNA"/ingreso antes de implementar (ver SPEC.md) |

## Open Questions

Ninguna pendiente — las 3 preguntas abiertas del SPEC ya se resolvieron con el usuario (ver SPEC.md, sección "Decisiones resueltas").
