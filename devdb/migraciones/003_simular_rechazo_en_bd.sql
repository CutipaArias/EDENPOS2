-- 003: el ajuste "Simular rechazo" de la facturación electrónica simulada pasa de settings.json a la base.
--
-- NO hace falta ejecutar este archivo: Edén POS agrega la columna solo al iniciar (AppData.Initialize).
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez.

USE edenpos;

ALTER TABLE configuracion ADD COLUMN simular_rechazo_comprobantes TINYINT(1) NOT NULL DEFAULT 0;
