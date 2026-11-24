CREATE DATABASE IF NOT EXISTS edenpos CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
USE edenpos;

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
  intentos_fallidos INT NOT NULL DEFAULT 0,
  bloqueado_en DATETIME NULL,
  FOREIGN KEY (id_rol) REFERENCES rol(id_rol)
);

CREATE TABLE cliente (
  dni CHAR(8) PRIMARY KEY,
  nombre_completo VARCHAR(150) NOT NULL,
  origen VARCHAR(60) NOT NULL,
  telefono VARCHAR(15) NULL,
  consentimiento_datos TINYINT(1) NOT NULL DEFAULT 0,
  consentimiento_fecha DATETIME NULL,
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
  stock_minimo INT NOT NULL DEFAULT 0,
  cargo_perdida DECIMAL(10,2) NOT NULL DEFAULT 0,
  UNIQUE KEY uq_producto_nombre_categoria (nombre, categoria)
);

CREATE TABLE producto_variante (
  id_variante CHAR(32) PRIMARY KEY,
  id_producto CHAR(32) NOT NULL,
  nombre VARCHAR(80) NOT NULL,
  orden INT NOT NULL DEFAULT 0,
  precio DECIMAL(10,2) NOT NULL,
  controla_inventario TINYINT(1) NOT NULL DEFAULT 0,
  stock INT NOT NULL DEFAULT 0,
  stock_inicial INT NOT NULL DEFAULT 0,
  stock_minimo INT NOT NULL DEFAULT 0,
  FOREIGN KEY (id_producto) REFERENCES producto(id_producto)
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

CREATE TABLE casillero_llave (
  id_ticket CHAR(32) NOT NULL,
  numero INT NOT NULL,
  PRIMARY KEY (id_ticket, numero),
  FOREIGN KEY (id_ticket) REFERENCES ticket_guardado(id_ticket)
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
  id_usuario INT NULL,
  empleado VARCHAR(60) NOT NULL DEFAULT '',
  fecha_apertura DATETIME NOT NULL,
  fecha_cierre DATETIME NULL,
  monto_inicial DECIMAL(10,2) NOT NULL,
  monto_cierre DECIMAL(10,2) NULL,
  monto_esperado DECIMAL(10,2) NULL,
  abierta TINYINT(1) NOT NULL DEFAULT 1,
  observacion_cierre VARCHAR(200) NULL,
  CONSTRAINT fk_sesion_caja_usuario FOREIGN KEY (id_usuario) REFERENCES usuario(id_usuario) ON DELETE SET NULL
);

CREATE TABLE movimiento_caja (
  id_movimiento CHAR(32) PRIMARY KEY,
  id_sesion CHAR(32) NOT NULL,
  id_usuario INT NULL,
  empleado VARCHAR(60) NOT NULL DEFAULT '',
  tipo VARCHAR(10) NOT NULL,
  importe DECIMAL(10,2) NOT NULL,
  comentario VARCHAR(150) NOT NULL,
  created_at DATETIME NOT NULL,
  FOREIGN KEY (id_sesion) REFERENCES sesion_caja(id_sesion),
  CONSTRAINT fk_movimiento_caja_usuario FOREIGN KEY (id_usuario) REFERENCES usuario(id_usuario) ON DELETE SET NULL
);

CREATE TABLE recibo (
  id_recibo CHAR(32) PRIMARY KEY,
  numero INT NOT NULL,
  id_sesion CHAR(32) NULL,
  created_at DATETIME NOT NULL,
  empleado VARCHAR(60) NOT NULL,
  cliente_nombre VARCHAR(150) NOT NULL,
  cliente_dni CHAR(8) NULL,
  medio_pago VARCHAR(10) NOT NULL,
  monto_recibido DECIMAL(10,2) NOT NULL,
  monto_efectivo DECIMAL(10,2) NOT NULL,
  monto_yape DECIMAL(10,2) NOT NULL,
  descuento_promocion DECIMAL(10,2) NOT NULL DEFAULT 0,
  id_promocion_aplicada VARCHAR(30) NULL,
  total DECIMAL(10,2) NOT NULL,
  estado VARCHAR(12) NOT NULL,
  refunded_at DATETIME NULL,
  refund_reason VARCHAR(200) NULL,
  FOREIGN KEY (id_sesion) REFERENCES sesion_caja(id_sesion)
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

CREATE TABLE comprobante (
  id_comprobante CHAR(32) PRIMARY KEY,
  id_recibo CHAR(32) NOT NULL UNIQUE,
  serie CHAR(4) NOT NULL,
  numero INT NOT NULL,
  fecha_emision DATETIME NOT NULL,
  estado_envio VARCHAR(12) NOT NULL,
  codigo_respuesta VARCHAR(10) NULL,
  intentos TINYINT NOT NULL DEFAULT 0,
  ultimo_intento DATETIME NULL,
  FOREIGN KEY (id_recibo) REFERENCES recibo(id_recibo),
  UNIQUE KEY uq_comprobante_serie_numero (serie, numero)
);

CREATE TABLE movimiento_inventario (
  id_movimiento CHAR(32) PRIMARY KEY,
  secuencia BIGINT NOT NULL AUTO_INCREMENT UNIQUE,
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

CREATE TABLE auditoria (
  id_auditoria BIGINT PRIMARY KEY AUTO_INCREMENT,
  id_usuario INT NULL,
  accion VARCHAR(60) NOT NULL,
  detalle VARCHAR(300) NOT NULL,
  created_at DATETIME NOT NULL,
  FOREIGN KEY (id_usuario) REFERENCES usuario(id_usuario)
);

CREATE TABLE configuracion (
  id TINYINT PRIMARY KEY DEFAULT 1,
  store_name VARCHAR(80) NOT NULL,
  employee_name VARCHAR(80) NOT NULL,
  currency_symbol VARCHAR(5) NOT NULL,
  next_receipt_number INT NOT NULL,
  next_shift_number INT NOT NULL,
  dni_lookup_enabled TINYINT(1) NOT NULL DEFAULT 0,
  dni_api_token_protected VARCHAR(500) NULL,
  promo_sexta_visita_activa TINYINT(1) NOT NULL DEFAULT 1,
  promo_grupal_minimo_personas INT NOT NULL DEFAULT 4,
  promo_grupal_porcentaje DECIMAL(5,2) NOT NULL DEFAULT 10,
  simular_rechazo_comprobantes TINYINT(1) NOT NULL DEFAULT 0,
  cargo_perdida_llave DECIMAL(10,2) NOT NULL DEFAULT 0,
  llaves_fuera_uso VARCHAR(120) NOT NULL DEFAULT '',
  CONSTRAINT chk_configuracion_single_row CHECK (id = 1)
);

-- Impresora termica de recibos por PC (RF-12 / RNF-07). Configuracion local: no entra en copias ni en la replica.
CREATE TABLE impresora_pc (
  equipo VARCHAR(64) PRIMARY KEY,
  nombre_impresora VARCHAR(200) NULL,
  imprimir_al_cobrar TINYINT(1) NOT NULL DEFAULT 0
);
