-- 007: caja por medio de pago (RF-04, RF-05 y RN-09 aplicada a ambos medios).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega las columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   movimiento_caja.medio:     'Efectivo' o 'Yape'. Solo los movimientos de Efectivo cambian el efectivo esperado del cajón;
--                              los de Yape cambian el Yape esperado. Los movimientos anteriores quedan como 'Efectivo'.
--   sesion_caja.yape_esperado: Yape esperado al cerrar (ventas Yape y la parte Yape de las mixtas, sin los recibos reembolsados,
--                              más los movimientos por Yape). NULL en turnos abiertos o cerrados antes de esta versión.
--   sesion_caja.yape_contado:  Yape confirmado al cerrar (lo que la dueña ve en su app Yape). NULL igual que el anterior.
--
-- Si el efectivo contado o el Yape confirmado no cuadran, la observación del cierre (observacion_cierre, 002) es obligatoria.

USE edenpos;

ALTER TABLE movimiento_caja ADD COLUMN medio VARCHAR(10) NOT NULL DEFAULT 'Efectivo';
ALTER TABLE sesion_caja ADD COLUMN yape_esperado DECIMAL(10,2) NULL;
ALTER TABLE sesion_caja ADD COLUMN yape_contado DECIMAL(10,2) NULL;
