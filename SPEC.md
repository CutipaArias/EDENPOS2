# SPEC: Completar EdenPOS contra FD03/FD04 + base SQL + refresco de diseño

## Objetivo

EdenPOS es la app de escritorio (WinForms, .NET Framework 4.8) que implementa el "Sistema Web de Gestión Administrativa" documentado en FD01/FD03/FD04 para el Sauna El Edén Cono Sur. Una revisión del código contra esos documentos encontró que EdenPOS: (a) guarda todo en archivos JSON en vez de una base SQL, y (b) no implementa varias funciones que FD03/FD04 dan por hechas: promociones, comprobantes electrónicos, auditoría, stock mínimo por producto y una pantalla de reportes.

Este spec cierra esas brechas **sin** convertir la app en el sistema web ASP.NET Core MVC que describe FD04 — se mantiene como app de escritorio WinForms; solo la capa de datos migra a SQL. También incluye un refresco visual guiado por los principios de las guías de interfaz de Apple que son aplicables a una app WinForms (ver sección de Diseño).

Éxito = cada módulo del mapa de capacidades (abajo) compila, pasa su propia verificación manual, y cierra la brecha de FD03/FD04 que lo motivó.

## Mapa de módulos (aprobado con el usuario)

| Módulo | Responsabilidad | Depende de |
|---|---|---|
| `sql-migration` | Mover el almacenamiento de `AppData` de JSON a MySQL, misma API pública | — |
| `promotions` | Sexta visita gratis + descuento grupal/familiar (FD03 RF-11/RF-22, RN-03/04/05) | `sql-migration` |
| `e-invoicing` | `IEmisorElectronico` + adaptador stub, estado de envío, regla DNI>S/700 (RF-12/21, RN-10/11) | `sql-migration` |
| `audit-trail` | Tabla de auditoría + registro en operaciones sensibles (RNF-04/06) | `sql-migration` |
| `stock-minimo` | Campo de stock mínimo por producto + alertas (RF-14) | `sql-migration` |
| `reports` | Pantalla de reportes caja/ventas/inventario por fecha (RF-17) | `sql-migration`, `promotions`, `e-invoicing` |
| `design-pass` | Refresco visual (tipografía, espaciado, feedback) vía `Theme.cs` | — (en paralelo) |

Orden de construcción: `sql-migration` → (`promotions`, `e-invoicing`, `audit-trail`, `stock-minimo`) → `reports`. `design-pass` puede ir en paralelo en cualquier momento.

## Decisiones ya tomadas (no reabrir)

1. **Motor SQL: MySQL** (Community, 8.4 LTS), igual que documenta FD04 §3.2.6. Instalación local, sin hosting (RE-03).
2. **Acceso a datos: MySqlConnector + ADO.NET plano**, no EF Core. Razón: EdenPOS ya tiene una fachada estática (`AppData`) con ~30 métodos públicos que todas las vistas (`SalesView`, `ProductsView`, `ShiftView`, etc.) consumen directamente; migrar a SQL manteniendo esas firmas es un cambio de una sola capa. EF Core añadiría migraciones, contexto, mapeos y una curva de configuración que no compensa para ~15 tablas pequeñas sin necesidad real de LINQ-to-SQL. ADO.NET + `MySqlConnector` (paquete NuGet, async, sin dependencias de MySQL Connector/NET con licencia GPL) es más simple de depurar y de instalar.
3. **La UI sigue siendo WinForms.** No se reescribe como ASP.NET Core MVC. FD04 describe esa arquitectura para un contexto distinto (servidor web + Kestrel); el usuario pidió completar *este* proyecto, no reemplazarlo.
4. **No hay conexión real a SUNAT/OSE.** No existen credenciales ni acceso a un operador de servicios electrónicos para este proyecto académico. El "adaptador" de facturación electrónica es un *stub* que simula el comportamiento documentado (encolar como `Pendiente`, aceptar o rechazar tras un intento) — exactamente lo que FD04 anticipa al poner `IEmisorElectronico` como frontera de Adapter ("el mecanismo definitivo con SUNAT aún está por validar").
5. **Promociones sin reinventar "Visita".** FD03/FD04 modelan una entidad `Visita` con ciclo de vida (ingreso→consumo→cierre) que EdenPOS no tiene — y no se va a construir ahora: implicaría rehacer todo `SalesView`/`SavedTicket`. En su lugar, el conteo de "sexta visita" se deriva del historial de `Receipt` por `CustomerDni` (ya existe). Esto cumple la regla de negocio (RN-03) sin el rediseño de UX que la entidad completa exigiría. Se documenta como decisión, no como omisión.

## Esquema SQL (módulo `sql-migration`)

Tablas, adaptadas de FD04 §3.2.6 a lo que EdenPOS usa realmente (se omiten `servicio` y `visita` por la decisión §5 arriba; `producto` cubre tanto productos como "servicios" vendibles, igual que hoy en el JSON):

```sql
CREATE TABLE rol (
  id_rol INT PRIMARY KEY AUTO_INCREMENT,
  nombre VARCHAR(30) NOT NULL UNIQUE
);

CREATE TABLE usuario (
  id_usuario INT PRIMARY KEY AUTO_INCREMENT,
  id_rol INT NOT NULL,
  nombre VARCHAR(60) NOT NULL,
  pin_hash VARCHAR(255) NOT NULL,
  pin_salt VARCHAR(255) NOT NULL,
  activo TINYINT(1) NOT NULL DEFAULT 1,
  created_at DATETIME NOT NULL,
  updated_at DATETIME NOT NULL,
  FOREIGN KEY (id_rol) REFERENCES rol(id_rol)
);

CREATE TABLE cliente (
  dni CHAR(8) PRIMARY KEY,
  nombre_completo VARCHAR(150) NOT NULL,
  origen VARCHAR(20) NOT NULL,
  created_at DATETIME NOT NULL,
  updated_at DATETIME NOT NULL
);

CREATE TABLE producto (
  id_producto CHAR(32) PRIMARY KEY,
  categoria VARCHAR(40) NOT NULL,
  nombre VARCHAR(60) NOT NULL,
  precio DECIMAL(10,2) NOT NULL,
  imagen VARCHAR(255) NULL,
  color_hex CHAR(7) NOT NULL,
  activo TINYINT(1) NOT NULL DEFAULT 1,
  controla_inventario TINYINT(1) NOT NULL DEFAULT 0,
  stock INT NOT NULL DEFAULT 0,
  stock_inicial INT NOT NULL DEFAULT 0,
  stock_minimo INT NOT NULL DEFAULT 0,          -- NUEVO (módulo stock-minimo)
  UNIQUE KEY uq_producto_nombre_categoria (nombre, categoria)
);

CREATE TABLE producto_variante (
  id_variante CHAR(32) PRIMARY KEY,
  id_producto CHAR(32) NOT NULL,
  nombre VARCHAR(80) NOT NULL,
  precio DECIMAL(10,2) NOT NULL,
  controla_inventario TINYINT(1) NOT NULL DEFAULT 0,
  stock INT NOT NULL DEFAULT 0,
  stock_inicial INT NOT NULL DEFAULT 0,
  stock_minimo INT NOT NULL DEFAULT 0,          -- NUEVO
  FOREIGN KEY (id_producto) REFERENCES producto(id_producto)
);

CREATE TABLE casillero_llave (                   -- resuelve llaves usadas en saved_ticket (JSON array hoy)
  id_ticket CHAR(32) NOT NULL,
  numero INT NOT NULL,
  PRIMARY KEY (id_ticket, numero)
);

CREATE TABLE ticket_guardado (
  id_ticket CHAR(32) PRIMARY KEY,
  nombre VARCHAR(80) NOT NULL,
  cliente_nombre VARCHAR(150) NULL,
  cliente_dni CHAR(8) NULL,
  adultos INT NOT NULL DEFAULT 0,
  ninos INT NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL,
  updated_at DATETIME NOT NULL
);

CREATE TABLE ticket_linea (
  id_ticket CHAR(32) NOT NULL,
  orden INT NOT NULL,
  id_producto CHAR(32) NULL,
  id_variante CHAR(32) NULL,
  nombre VARCHAR(120) NOT NULL,
  comentario VARCHAR(150) NULL,
  precio_unitario DECIMAL(10,2) NOT NULL,
  cantidad INT NOT NULL,
  PRIMARY KEY (id_ticket, orden),
  FOREIGN KEY (id_ticket) REFERENCES ticket_guardado(id_ticket)
);

CREATE TABLE sesion_caja (
  id_sesion CHAR(32) PRIMARY KEY,
  numero INT NOT NULL,
  id_usuario INT NOT NULL,
  fecha_apertura DATETIME NOT NULL,
  fecha_cierre DATETIME NULL,
  monto_inicial DECIMAL(10,2) NOT NULL,
  monto_cierre DECIMAL(10,2) NULL,
  monto_esperado DECIMAL(10,2) NULL,
  abierta TINYINT(1) NOT NULL DEFAULT 1,
  FOREIGN KEY (id_usuario) REFERENCES usuario(id_usuario)
);

CREATE TABLE movimiento_caja (
  id_movimiento CHAR(32) PRIMARY KEY,
  id_sesion CHAR(32) NOT NULL,
  id_usuario INT NOT NULL,
  tipo VARCHAR(10) NOT NULL,                    -- Entrada / Salida
  importe DECIMAL(10,2) NOT NULL,
  comentario VARCHAR(150) NOT NULL,
  created_at DATETIME NOT NULL,
  FOREIGN KEY (id_sesion) REFERENCES sesion_caja(id_sesion)
);

CREATE TABLE recibo (
  id_recibo CHAR(32) PRIMARY KEY,
  numero INT NOT NULL,
  id_sesion CHAR(32) NULL,
  created_at DATETIME NOT NULL,
  empleado VARCHAR(60) NOT NULL,
  cliente_nombre VARCHAR(150) NOT NULL,
  cliente_dni CHAR(8) NULL,
  medio_pago VARCHAR(10) NOT NULL,              -- Efectivo / Yape / Mixto
  monto_recibido DECIMAL(10,2) NOT NULL,
  monto_efectivo DECIMAL(10,2) NOT NULL,
  monto_yape DECIMAL(10,2) NOT NULL,
  descuento_promocion DECIMAL(10,2) NOT NULL DEFAULT 0,  -- NUEVO (módulo promotions)
  id_promocion_aplicada VARCHAR(30) NULL,                -- NUEVO ("SextaVisita" / "Grupal" / NULL)
  total DECIMAL(10,2) NOT NULL,
  estado VARCHAR(12) NOT NULL,                  -- Pagado / Reembolsado
  refunded_at DATETIME NULL,
  refund_reason VARCHAR(200) NULL
);

CREATE TABLE recibo_linea (
  id_recibo CHAR(32) NOT NULL,
  orden INT NOT NULL,
  id_producto CHAR(32) NULL,
  id_variante CHAR(32) NULL,
  nombre VARCHAR(120) NOT NULL,
  comentario VARCHAR(150) NULL,
  precio_unitario DECIMAL(10,2) NOT NULL,
  cantidad INT NOT NULL,
  PRIMARY KEY (id_recibo, orden),
  FOREIGN KEY (id_recibo) REFERENCES recibo(id_recibo)
);

CREATE TABLE comprobante (                       -- NUEVO (módulo e-invoicing)
  id_comprobante CHAR(32) PRIMARY KEY,
  id_recibo CHAR(32) NOT NULL UNIQUE,
  serie CHAR(4) NOT NULL,
  numero INT NOT NULL,
  fecha_emision DATETIME NOT NULL,
  estado_envio VARCHAR(12) NOT NULL,            -- Pendiente / Aceptado / Rechazado
  codigo_respuesta VARCHAR(10) NULL,
  intentos TINYINT NOT NULL DEFAULT 0,
  ultimo_intento DATETIME NULL,
  FOREIGN KEY (id_recibo) REFERENCES recibo(id_recibo),
  UNIQUE KEY uq_comprobante_serie_numero (serie, numero)
);

CREATE TABLE movimiento_inventario (
  id_movimiento CHAR(32) PRIMARY KEY,
  id_producto CHAR(32) NULL,
  id_variante CHAR(32) NULL,
  producto_nombre VARCHAR(120) NOT NULL,
  tipo VARCHAR(20) NOT NULL,
  cantidad INT NOT NULL,
  stock_previo INT NOT NULL,
  stock_nuevo INT NOT NULL,
  nota VARCHAR(150) NOT NULL,
  referencia_id CHAR(32) NULL,
  created_at DATETIME NOT NULL
);

CREATE TABLE auditoria (                         -- NUEVO (módulo audit-trail)
  id_auditoria BIGINT PRIMARY KEY AUTO_INCREMENT,
  id_usuario INT NULL,
  accion VARCHAR(60) NOT NULL,
  detalle VARCHAR(300) NOT NULL,
  created_at DATETIME NOT NULL,
  FOREIGN KEY (id_usuario) REFERENCES usuario(id_usuario)
);

CREATE TABLE configuracion (                      -- reemplaza settings.json (fila única)
  id TINYINT PRIMARY KEY DEFAULT 1,
  store_name VARCHAR(80) NOT NULL,
  employee_name VARCHAR(80) NOT NULL,
  currency_symbol VARCHAR(5) NOT NULL,
  next_receipt_number INT NOT NULL,
  next_shift_number INT NOT NULL,
  dni_lookup_enabled TINYINT(1) NOT NULL DEFAULT 0,
  dni_api_token_protected VARCHAR(500) NULL,
  promo_sexta_visita_activa TINYINT(1) NOT NULL DEFAULT 1,   -- NUEVO
  promo_grupal_minimo_personas INT NOT NULL DEFAULT 4,       -- NUEVO
  promo_grupal_porcentaje DECIMAL(5,2) NOT NULL DEFAULT 10,  -- NUEVO
  CONSTRAINT chk_configuracion_single_row CHECK (id = 1)
);
```

**Capa de acceso a datos:** `Infrastructure.cs` (`AppData`) deja de leer/escribir JSON y pasa a abrir una conexión `MySqlConnection` por operación (como hoy abre/cierra el archivo por operación). Los métodos públicos (`Products`, `Receipts`, `AddReceipt`, `OpenNewShift`, etc.) conservan su firma; por dentro ejecutan SQL parametrizado en vez de `Save`/`Load` de JSON. El `lock (Sync)` en C# se mantiene como protección en memoria dentro del mismo proceso; además cada tabla usa transacciones (`BEGIN`/`COMMIT`) para las operaciones multi-tabla (p. ej. `AddReceipt` escribe en `recibo`, `recibo_linea`, `producto`/`producto_variante` y `movimiento_inventario` en una sola transacción).

**Migración de datos existentes:** herramienta de arranque único (`--migrate-json` o similar) que lee los JSON actuales de `DataDirectory` y los inserta en las tablas nuevas, para no perder lo que ya haya capturado el sauna.

**Cadena de conexión:** servidor `localhost`, base `edenpos`, usuario y contraseña configurables desde `SettingsView` (igual que hoy se configura `data.path`), guardados protegidos con `AppData.ProtectSecret` (DPAPI, ya existe).

## Módulo `promotions`

- `PromocionCalculadora` (clase nueva) con método `Calcular(string clienteDni, List<ReceiptLine> lineas, out decimal descuento, out string promocionAplicada)`.
- **Sexta visita gratis (RN-03):** cuenta recibos `Pagado` previos del mismo `clienteDni` (requiere DNI, cliente anónimo no califica). Cada 5 visitas pagadas, la 6ª exonera la línea de categoría `SAUNA` (ingreso/admisión) de mayor precio en ese recibo. Los consumos y demás líneas de esa visita sí se cobran (texto literal de RN-03).
- **Descuento grupal/familiar (RN-04):** si la cantidad de personas (adultos+niños inferidos por `OccupancyCalculator`, ya existe) ≥ `promo_grupal_minimo_personas` (configurable), aplica `promo_grupal_porcentaje`% de descuento al total. Configurable desde `SettingsView`, no hardcodeado.
- **No acumulables (RN-05):** si ambas aplican, se usa la de mayor beneficio para el cliente (se calculan las dos y se toma el descuento mayor).
- Patrón: `Promocion` abstracta + `SextaVisitaGratis`/`DescuentoGrupal` concretas (Strategy, como pide FD04), pero sin la complejidad de un registro de plugins — un `switch`/lista fija basta para 2 estrategias.
- UI: `CheckoutDialog` (`Dialogs.cs`) muestra el descuento aplicado y qué promoción fue, antes de confirmar el cobro.

## Módulo `e-invoicing`

- `IEmisorElectronico` con `Task<ResultadoEnvio> EnviarAsync(Comprobante comprobante)` y `Task<ResultadoEnvio> ConsultarEstadoAsync(string serie, int numero)`.
- `EmisorElectronicoStub : IEmisorElectronico` — implementación simulada: acepta el comprobante inmediatamente (configurable a "simular rechazo" desde Settings para poder probar el flujo de reintento sin depender de un servicio real).
- `Comprobante` nuevo modelo (tabla `comprobante` arriba) ligado 1 a 1 con `Receipt`. Al confirmar un cobro, se crea el comprobante en estado `Pendiente`, se llama al adaptador, y el resultado actualiza `estado_envio`.
- **Reintento (RF-21):** dado que esto es una app de escritorio sin servicio de Windows propio, un `System.Threading.Timer` cada 5 minutos mientras la app está abierta reintenta los comprobantes `Pendiente`/`Rechazado` — es la opción más simple consistente con lo que describe FD04 y no exige una pantalla de administración de Windows Services. Se documenta como decisión, no se construye un background worker separado.
- **RN-11 (DNI obligatorio >S/700):** validación en `AddReceipt` (o antes, en el diálogo de cobro): si `Total > 700` y `customerDni` vacío, bloquear el cobro y pedirlo.

## Módulo `audit-trail`

Tabla `auditoria` (arriba). Se agrega `AppData.RegistrarAuditoria(accion, detalle)` (dentro del mismo `lock`/transacción) llamado desde:
- `DeleteProduct`, `RefundReceipt`, `CloseShift` (solo si hay diferencia ≠ 0), `DeleteUser`, `SetUserActive(false)`, y el nuevo flujo de rechazo de comprobante.
- Pantalla de consulta simple (grid de solo lectura) en `SettingsView` o una nueva vista, filtrable por fecha/usuario.

## Módulo `stock-minimo`

- Campo `StockMinimo` en `Product`/`ProductOption` (ya reflejado en el esquema SQL arriba).
- `ProductEditorDialog`: nuevo campo numérico junto al checkbox de "Controla inventario" (del fix anterior), visible solo si el checkbox está marcado.
- `ProductsView.LoadProducts()`: cambiar `product.Stock <= 3` (hardcodeado) por `product.Stock <= product.StockMinimo`.

## Módulo `reports`

Nueva vista `ReportsView` con selector de rango de fechas y tres pestañas/secciones:
1. **Caja:** turnos del rango, apertura/cierre/diferencia por turno.
2. **Ventas:** total vendido, por medio de pago, por categoría de producto, promociones aplicadas y su descuento total.
3. **Inventario:** movimientos del rango por producto (entradas/salidas/ajustes) y productos que llegaron a stock mínimo.

Todo son consultas `SELECT` de solo lectura contra las tablas nuevas — no requiere lógica de negocio nueva más allá de agregaciones SQL.

## Diseño (principios de las guías de interfaz de Apple aplicables a WinForms)

Esas guías están pensadas sobre todo para web y móvil (CSS, springs, `backdrop-filter`, Pointer Events). La mayoría de sus reglas de **gesto/animación** (interrumpibilidad, velocity handoff, rubber-banding, materiales translúcidos con blur) **no aplican**: WinForms/GDI+ no tiene compositor de blur accesible sin P/Invoke pesado a DWM, ni un sistema de springs — y no se va a construir uno para una app de punto de venta de un solo local. Lo que sí se adopta, porque son principios de diseño y no mecánica de gestos:

1. **Respuesta inmediata al presionar, no al soltar:** `Theme.Button` ya tiene `MouseOverBackColor`; se añade `FlatAppearance.MouseDownBackColor` (un tono más oscuro que `GreenDark`/`Border`) para que el botón reaccione en el instante del clic, no al soltarlo.
2. **Jerarquía tipográfica como conjunto, no solo tamaño:** definir una escala en `Theme.cs` (`Display`, `Title`, `Heading`, `Body`, `Caption`) combinando tamaño + peso + espaciado entre controles — no solo tamaño suelto como hoy. GDI+ no soporta tracking/letter-spacing real, así que esa parte de §15 no aplica; sí aplica la idea de "jerarquía = tamaño + peso + espaciado" juntos.
3. **Material = jerarquía, sin blur:** mantener la convención ya presente (`Canvas` para fondo, blanco para tarjetas, `GreenSoft` para selección/realce, `Green`/`GreenDark` para acciones primarias) pero auditarla por consistencia entre pantallas — hoy cada vista define colores/paddings ad hoc en vez de usar tokens de `Theme`.
4. **Etiquetas directas y específicas:** ya se cumple razonablemente ("REGISTRAR ENTRADA" en vez de "Acción"); revisar solo las pantallas nuevas que este spec agrega (Reportes, Auditoría, Promociones en Settings) para mantener el mismo estándar.
5. **Feedback por tipo:** las `MessageBox` ya diferencian Warning/Information; añadir un tercer caso explícito para "éxito/confirmación" (hoy se reusa Information para todo) en los flujos nuevos (comprobante aceptado, promoción aplicada).
6. **Espaciado consistente:** reemplazar los números de posición hardcodeados en cada diálogo (`SetBounds(270, 294, ...)`) por constantes de espaciado (`Theme.Spacing.Sm/Md/Lg`) donde se toquen pantallas por los otros módulos — no se va a reescribir el layout de pantallas que ningún módulo modifica, para no inflar el riesgo de esta fase.

**No se hace:** springs/animación de gestos, blur/translucidez, haptics, "momentum projection" — ninguno tiene sentido en un formulario WinForms sin gestos táctiles, y construir ese motor de animación no está dentro de lo que el usuario pidió (dijo "dale diseño", no "constrúyele un motor de animación").

## Alcance de verificación

No hay proyecto de pruebas automatizadas en EdenPOS (visto en la revisión de código anterior). Verificación por fase:
- `sql-migration`: `dotnet build` limpio + probar manualmente cada pantalla contra una instancia MySQL local (Docker o instalación nativa — a decidir cuando se implemente) + comparar que los datos migrados desde JSON coincidan.
- Cada módulo funcional: `dotnet build` limpio + prueba manual del flujo específico (ej. vender 6 visitas del mismo DNI y confirmar que la 6ª no cobra el ingreso).
- `design-pass`: captura de pantalla antes/después de las pantallas tocadas.

## Límites

- **Siempre:** compilar (`dotnet build`) antes de considerar una tarea terminada; mantener la API pública de `AppData` estable para no forzar cambios en cascada en las vistas que no son parte de la tarea actual.
- **Preguntar primero:** cualquier cambio a la contraseña/cadena de conexión de MySQL en producción; borrar datos JSON originales (se conservan como respaldo hasta confirmar que la migración fue exitosa); instalar o iniciar servicios del sistema (MySQL Server, Docker Desktop).
- **Nunca:** inventar credenciales reales de SUNAT/OSE o simular que la facturación electrónica es real ante el usuario — el stub debe decir claramente "simulado" en la UI/configuración.

## Decisiones resueltas con el usuario (reemplazan las preguntas abiertas)

1. **Qué cuenta como "visita/ingreso" para la sexta visita gratis:** la categoría de producto **`SAUNA`** (ya existe en el catálogo por defecto: GENERAL, ADULTO MAYOR, AMIGOS X3/4/5, FAMILIA 1/2, PAREJA, NIÑO, VIP, PASE CORTESÍA — son exactamente los productos de ingreso/admisión, los mismos que ya reconoce `OccupancyCalculator`). La sexta visita paga exonera la línea de categoría `SAUNA` de mayor precio en ese recibo; el resto de líneas (consumos, alquileres, cafetería) se cobran normal.
2. **Cómo se levanta MySQL para desarrollo/pruebas:** instalación nativa de **MySQL Server** como servicio de Windows en esta máquina (no Docker). Es lo mismo que quedaría corriendo en producción en el sauna.
3. **Dónde va la pantalla de Auditoría:** pantalla nueva e independiente en el menú principal (no una pestaña dentro de Configuración).

## Ajustes de esquema aprobados durante la Fase 1 (2026-11-16)

El esquema vigente es `devdb/schema.sql`; estos cambios prevalecen sobre los `CREATE TABLE` de arriba.

1. **`sesion_caja` y `movimiento_caja`:** `id_usuario` pasa a `INT NULL` (FK con `ON DELETE SET NULL`) y se añade `empleado VARCHAR(60) NOT NULL DEFAULT ''`. La app guarda el nombre del empleado en cada turno y movimiento, no un id; así el historial conserva quién lo hizo aunque el usuario se renombre o elimine, y se pueden importar turnos antiguos del JSON.
2. **`producto_variante`:** se añade `orden INT NOT NULL DEFAULT 0` para conservar el orden en que se crearon las variantes.
3. **`movimiento_inventario`:** se añade `secuencia BIGINT AUTO_INCREMENT UNIQUE` para ordenar de forma estable los movimientos del mismo segundo.
4. **`cliente.origen`:** pasa de `VARCHAR(20)` a `VARCHAR(60)`; los datos reales tienen orígenes de 34 caracteres.
5. **Servidor de la app:** MariaDB 10.4 de XAMPP en el puerto 3306 (decisión del usuario), visible en phpMyAdmin. MySQL 8.4 queda solo como servidor de pruebas en el puerto 3307. El esquema `devdb/schema.sql` se aplicó sin cambios en MariaDB.


## Notas de implementación

- **Regla de visitas (RN-03):** ciclo 5+1, calculado como visitas pagadas − 5 × visitas gratis ≥ 5. La categoría SAUNA se evalúa con el catálogo actual de productos, así que cambiar la categoría de un producto o borrarlo afecta al historial de visitas.
- **Recibos importados de JSON antiguos** no tienen comprobante electrónico.
- **Facturación simulada:** el emisor es `EmisorElectronicoStub`; un emisor real se conecta implementando `IEmisorElectronico` y asignándolo a `ElectronicInvoicingService.Emisor`. Los cambios de estado se avisan con `AppData.ComprobantesChanged` (puede llegar desde un hilo de fondo).
- **Usuarios con auditoría** no se pueden eliminar, solo desactivar.
- **La sesión de MySQL fuerza `sql_mode` estricto.**
