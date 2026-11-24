-- =====================================================================
-- EdenPOS - Replica en Supabase (PostgreSQL)
-- Ejecutar UNA VEZ en Supabase > SQL Editor (como usuario postgres).
-- Es idempotente: se puede volver a ejecutar sin perder datos.
--
-- ANTES DE EJECUTAR: busque la linea  v_password text := 'CAMBIE_ESTA_CONTRASEÑA';
-- (en el paso 2) y reemplace CAMBIE_ESTA_CONTRASEÑA por una contraseña propia,
-- larga y sin comillas simples. El script se detiene si la deja sin cambiar.
--
-- Notas de diseno:
--  * Mismos nombres de tabla y columna que MariaDB (devdb/schema.sql).
--  * SIN claves foraneas ni UNIQUE adicionales: la replica sube las tablas en
--    cualquier orden y MariaDB ya garantiza la integridad. Solo claves primarias.
--  * CHAR(n) de MariaDB -> varchar(n) para evitar relleno con espacios.
--  * RLS activo: solo el rol edenpos_replica puede leer/escribir. anon,
--    authenticated y public no tienen ningun acceso (la API publica no expone nada).
-- =====================================================================

-- 1. Esquema ----------------------------------------------------------
CREATE SCHEMA IF NOT EXISTS edenpos;

-- 2. Rol de la replica ------------------------------------------------
DO $$
DECLARE
  v_password text := 'CAMBIE_ESTA_CONTRASEÑA';   -- <-- CAMBIAR AQUI
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'edenpos_replica') THEN
    IF v_password = 'CAMBIE_ESTA_CONTRASEÑA' THEN
      RAISE EXCEPTION 'Edite el script: reemplace CAMBIE_ESTA_CONTRASEÑA por una contraseña propia antes de ejecutarlo.';
    END IF;
    EXECUTE format('CREATE ROLE edenpos_replica LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', v_password);
  END IF;
END
$$;
-- Limites del rol (idempotentes; se reaplican en cada ejecucion): como maximo 5
-- conexiones simultaneas y 60 s por consulta, para que un fallo no sature el proyecto.
ALTER ROLE edenpos_replica CONNECTION LIMIT 5;
ALTER ROLE edenpos_replica SET statement_timeout = '60s';

-- Para cambiar la contraseña mas adelante:
--   ALTER ROLE edenpos_replica WITH PASSWORD 'nueva_contraseña';

-- 3. Tablas -----------------------------------------------------------
CREATE TABLE IF NOT EXISTS edenpos.rol (
  id_rol integer NOT NULL PRIMARY KEY,
  nombre varchar(30) NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.usuario (
  id_usuario integer NOT NULL PRIMARY KEY,
  id_rol integer NOT NULL,
  nombre varchar(60) NOT NULL,
  pin_hash varchar(255) NOT NULL,
  pin_salt varchar(255) NOT NULL,
  activo boolean NOT NULL DEFAULT true,
  created_at timestamp without time zone NOT NULL,
  updated_at timestamp without time zone NOT NULL
);

-- Bloqueo de cuentas por intentos fallidos (RF-01). Idempotente (PostgreSQL admite ADD COLUMN IF NOT EXISTS). La app NO replica estas dos
-- columnas (el bloqueo es local de cada instalación); existen aquí solo para que el esquema coincida con el de MariaDB/MySQL.
ALTER TABLE edenpos.usuario ADD COLUMN IF NOT EXISTS intentos_fallidos integer NOT NULL DEFAULT 0;
ALTER TABLE edenpos.usuario ADD COLUMN IF NOT EXISTS bloqueado_en timestamp without time zone NULL;

CREATE TABLE IF NOT EXISTS edenpos.cliente (
  dni varchar(8) NOT NULL PRIMARY KEY,
  nombre_completo varchar(150) NOT NULL,
  origen varchar(60) NOT NULL,
  created_at timestamp without time zone NOT NULL,
  updated_at timestamp without time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.producto (
  id_producto varchar(32) NOT NULL PRIMARY KEY,
  categoria varchar(40) NOT NULL,
  nombre varchar(60) NOT NULL,
  precio numeric(10,2) NOT NULL,
  imagen varchar(255) NULL,
  color_hex varchar(7) NOT NULL,
  activo boolean NOT NULL DEFAULT true,
  controla_inventario boolean NOT NULL DEFAULT false,
  stock integer NOT NULL DEFAULT 0,
  stock_inicial integer NOT NULL DEFAULT 0,
  stock_minimo integer NOT NULL DEFAULT 0
);

-- Cargo por pérdida del implemento (RF-19 / RN-07). Por ahora la app NO lo replica (trabajo local); la columna queda lista.
ALTER TABLE edenpos.producto ADD COLUMN IF NOT EXISTS cargo_perdida numeric(10,2) NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS edenpos.producto_variante (
  id_variante varchar(32) NOT NULL PRIMARY KEY,
  id_producto varchar(32) NOT NULL,
  nombre varchar(80) NOT NULL,
  orden integer NOT NULL DEFAULT 0,
  precio numeric(10,2) NOT NULL,
  controla_inventario boolean NOT NULL DEFAULT false,
  stock integer NOT NULL DEFAULT 0,
  stock_inicial integer NOT NULL DEFAULT 0,
  stock_minimo integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS edenpos.ticket_guardado (
  id_ticket varchar(32) NOT NULL PRIMARY KEY,
  nombre varchar(80) NOT NULL,
  cliente_nombre varchar(150) NULL,
  cliente_dni varchar(8) NULL,
  adultos integer NOT NULL DEFAULT 0,
  ninos integer NOT NULL DEFAULT 0,
  created_at timestamp without time zone NOT NULL,
  updated_at timestamp without time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.casillero_llave (
  id_ticket varchar(32) NOT NULL,
  numero integer NOT NULL,
  PRIMARY KEY (id_ticket, numero)
);

CREATE TABLE IF NOT EXISTS edenpos.ticket_linea (
  id_ticket varchar(32) NOT NULL,
  orden integer NOT NULL,
  id_producto varchar(32) NULL,
  id_variante varchar(32) NULL,
  nombre varchar(120) NOT NULL,
  comentario varchar(150) NULL,
  precio_unitario numeric(10,2) NOT NULL,
  cantidad integer NOT NULL,
  PRIMARY KEY (id_ticket, orden)
);

CREATE TABLE IF NOT EXISTS edenpos.sesion_caja (
  id_sesion varchar(32) NOT NULL PRIMARY KEY,
  numero integer NOT NULL,
  id_usuario integer NULL,
  empleado varchar(60) NOT NULL DEFAULT '',
  fecha_apertura timestamp without time zone NOT NULL,
  fecha_cierre timestamp without time zone NULL,
  monto_inicial numeric(10,2) NOT NULL,
  monto_cierre numeric(10,2) NULL,
  monto_esperado numeric(10,2) NULL,
  abierta boolean NOT NULL DEFAULT true
);

-- Observación del cierre de caja con diferencia (RN-09). Por ahora la app NO la replica (trabajo local); la columna queda lista.
ALTER TABLE edenpos.sesion_caja ADD COLUMN IF NOT EXISTS observacion_cierre varchar(200) NULL;

CREATE TABLE IF NOT EXISTS edenpos.movimiento_caja (
  id_movimiento varchar(32) NOT NULL PRIMARY KEY,
  id_sesion varchar(32) NOT NULL,
  id_usuario integer NULL,
  empleado varchar(60) NOT NULL DEFAULT '',
  tipo varchar(10) NOT NULL,
  importe numeric(10,2) NOT NULL,
  comentario varchar(150) NOT NULL,
  created_at timestamp without time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.recibo (
  id_recibo varchar(32) NOT NULL PRIMARY KEY,
  numero integer NOT NULL,
  id_sesion varchar(32) NULL,
  created_at timestamp without time zone NOT NULL,
  empleado varchar(60) NOT NULL,
  cliente_nombre varchar(150) NOT NULL,
  cliente_dni varchar(8) NULL,
  medio_pago varchar(10) NOT NULL,
  monto_recibido numeric(10,2) NOT NULL,
  monto_efectivo numeric(10,2) NOT NULL,
  monto_yape numeric(10,2) NOT NULL,
  descuento_promocion numeric(10,2) NOT NULL DEFAULT 0,
  id_promocion_aplicada varchar(30) NULL,
  total numeric(10,2) NOT NULL,
  estado varchar(12) NOT NULL,
  refunded_at timestamp without time zone NULL,
  refund_reason varchar(200) NULL
);

CREATE TABLE IF NOT EXISTS edenpos.recibo_linea (
  id_recibo varchar(32) NOT NULL,
  orden integer NOT NULL,
  id_producto varchar(32) NULL,
  id_variante varchar(32) NULL,
  nombre varchar(120) NOT NULL,
  comentario varchar(150) NULL,
  precio_unitario numeric(10,2) NOT NULL,
  cantidad integer NOT NULL,
  PRIMARY KEY (id_recibo, orden)
);

CREATE TABLE IF NOT EXISTS edenpos.comprobante (
  id_comprobante varchar(32) NOT NULL PRIMARY KEY,
  id_recibo varchar(32) NOT NULL,
  serie varchar(4) NOT NULL,
  numero integer NOT NULL,
  fecha_emision timestamp without time zone NOT NULL,
  estado_envio varchar(12) NOT NULL,
  codigo_respuesta varchar(10) NULL,
  intentos smallint NOT NULL DEFAULT 0,
  ultimo_intento timestamp without time zone NULL
);

CREATE TABLE IF NOT EXISTS edenpos.movimiento_inventario (
  id_movimiento varchar(32) NOT NULL PRIMARY KEY,
  secuencia bigint NOT NULL,
  id_producto varchar(32) NULL,
  id_variante varchar(32) NULL,
  producto_nombre varchar(120) NOT NULL,
  tipo varchar(20) NOT NULL,
  cantidad integer NOT NULL,
  stock_previo integer NOT NULL,
  stock_nuevo integer NOT NULL,
  nota varchar(150) NOT NULL,
  referencia_id varchar(32) NULL,
  created_at timestamp without time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.auditoria (
  id_auditoria bigint NOT NULL PRIMARY KEY,
  id_usuario integer NULL,
  accion varchar(60) NOT NULL,
  detalle varchar(300) NOT NULL,
  created_at timestamp without time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS edenpos.configuracion (
  id smallint NOT NULL PRIMARY KEY DEFAULT 1,
  store_name varchar(80) NOT NULL,
  employee_name varchar(80) NOT NULL,
  currency_symbol varchar(5) NOT NULL,
  next_receipt_number integer NOT NULL,
  next_shift_number integer NOT NULL,
  dni_lookup_enabled boolean NOT NULL DEFAULT false,
  dni_api_token_protected varchar(500) NULL,
  promo_sexta_visita_activa boolean NOT NULL DEFAULT true,
  promo_grupal_minimo_personas integer NOT NULL DEFAULT 4,
  promo_grupal_porcentaje numeric(5,2) NOT NULL DEFAULT 10,
  CONSTRAINT chk_configuracion_single_row CHECK (id = 1)
);

-- Cargo por pérdida de llave y llaves fuera de uso (RF-19 / RN-06 / RN-07). Por ahora la app NO los replica (trabajo local); las columnas quedan listas.
ALTER TABLE edenpos.configuracion ADD COLUMN IF NOT EXISTS cargo_perdida_llave numeric(10,2) NOT NULL DEFAULT 0;
ALTER TABLE edenpos.configuracion ADD COLUMN IF NOT EXISTS llaves_fuera_uso varchar(120) NOT NULL DEFAULT '';

-- Metadatos de la replica (ultima subida, identificador de la instalacion, etc.)
CREATE TABLE IF NOT EXISTS edenpos._replica_info (
  clave text NOT NULL PRIMARY KEY,
  valor text NULL
);

-- 4. Permisos y RLS ---------------------------------------------------
-- Nadie mas que edenpos_replica (y postgres, el dueño) toca el esquema.
REVOKE ALL ON SCHEMA edenpos FROM PUBLIC, anon, authenticated;
REVOKE ALL ON ALL TABLES IN SCHEMA edenpos FROM PUBLIC, anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA edenpos FROM PUBLIC, anon, authenticated;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA edenpos FROM PUBLIC, anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA edenpos REVOKE ALL ON TABLES FROM PUBLIC, anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA edenpos REVOKE ALL ON SEQUENCES FROM PUBLIC, anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA edenpos REVOKE ALL ON FUNCTIONS FROM PUBLIC, anon, authenticated;

GRANT USAGE ON SCHEMA edenpos TO edenpos_replica;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA edenpos TO edenpos_replica;
ALTER DEFAULT PRIVILEGES IN SCHEMA edenpos GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO edenpos_replica;

DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'rol','usuario','cliente','producto','producto_variante','ticket_guardado',
    'casillero_llave','ticket_linea','sesion_caja','movimiento_caja','recibo',
    'recibo_linea','comprobante','movimiento_inventario','auditoria',
    'configuracion','_replica_info']
  LOOP
    EXECUTE format('ALTER TABLE edenpos.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('DROP POLICY IF EXISTS edenpos_replica_all ON edenpos.%I', t);
    EXECUTE format('CREATE POLICY edenpos_replica_all ON edenpos.%I AS PERMISSIVE FOR ALL TO edenpos_replica USING (true) WITH CHECK (true)', t);
  END LOOP;
END
$$;

-- 5. Comprobacion rapida (debe devolver 17 filas, todas con rls_activo = true)
SELECT c.relname AS tabla, c.relrowsecurity AS rls_activo
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'edenpos' AND c.relkind = 'r'
ORDER BY c.relname;
