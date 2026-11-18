-- 001: bloqueo de cuentas por intentos fallidos (RF-01).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega estas columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. NO usa "ADD COLUMN IF NOT EXISTS" (MySQL 8.4 no lo admite), así que
-- ejecútelo UNA sola vez: una segunda ejecución falla con "Duplicate column name" (la app, en cambio, es idempotente).
--
--   intentos_fallidos: PIN incorrectos seguidos; vuelve a 0 con un PIN correcto, al desbloquear o al cambiar el PIN.
--   bloqueado_en:      momento del bloqueo (NULL = cuenta no bloqueada). Los trabajadores quedan bloqueados hasta que la dueña
--                      los desbloquee; la dueña se desbloquea sola a los 15 minutos (se calcula en la app).

USE edenpos;

ALTER TABLE usuario ADD COLUMN intentos_fallidos INT NOT NULL DEFAULT 0;
ALTER TABLE usuario ADD COLUMN bloqueado_en DATETIME NULL;
