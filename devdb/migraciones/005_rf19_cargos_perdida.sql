-- 005: devolución de llaves e implementos y cargos por pérdida (RF-19, RN-06, RN-07 y RF-22).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega las columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   configuracion.cargo_perdida_llave: monto que se suma al recibo por cada llave que el cliente no devuelve (0 = sin cargo,
--                                      pero la pérdida igual se registra en el recibo y en Auditoría).
--   configuracion.llaves_fuera_uso:    llaves no devueltas, separadas por comas (por ejemplo "3,17"). No se asignan a otra visita
--                                      hasta que la dueña las libera en Configuración > Cargos por pérdida (RN-06).
--   producto.cargo_perdida:            monto por unidad que se cobra si no se devuelve el implemento (toalla, sandalia...).
--                                      Las variantes usan el cargo de su artículo.

USE edenpos;

ALTER TABLE configuracion ADD COLUMN cargo_perdida_llave DECIMAL(10,2) NOT NULL DEFAULT 0;
ALTER TABLE configuracion ADD COLUMN llaves_fuera_uso VARCHAR(120) NOT NULL DEFAULT '';
ALTER TABLE producto ADD COLUMN cargo_perdida DECIMAL(10,2) NOT NULL DEFAULT 0;
