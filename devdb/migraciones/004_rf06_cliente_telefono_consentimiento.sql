-- 004: teléfono opcional y consentimiento de datos del cliente (RF-06, RN-14, RNF-09 - Ley N.º 29733).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega las columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   telefono:             celular del cliente (9 dígitos que empiezan en 9). NULL si no lo dio.
--   consentimiento_datos: 1 cuando el cliente autorizó el uso de sus datos; sin él la app no guarda DNI ni teléfono.
--                         Los clientes que ya estaban guardados quedan en 0.
--   consentimiento_fecha: momento en que se registró el consentimiento. NULL si no lo hay.

USE edenpos;

ALTER TABLE cliente ADD COLUMN telefono VARCHAR(15) NULL;
ALTER TABLE cliente ADD COLUMN consentimiento_datos TINYINT(1) NOT NULL DEFAULT 0;
ALTER TABLE cliente ADD COLUMN consentimiento_fecha DATETIME NULL;
