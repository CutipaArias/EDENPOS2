-- 002: observación obligatoria al cerrar caja con diferencia (RN-09).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega la columna solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existe.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   observacion_cierre: motivo que escribe quien cierra el turno cuando el efectivo contado no cuadra con el esperado.
--                       NULL cuando la caja cuadró.

USE edenpos;

ALTER TABLE sesion_caja ADD COLUMN observacion_cierre VARCHAR(200) NULL;
