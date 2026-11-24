-- 008: saldo pendiente autorizado por la dueña y su cobro posterior (RN-01), y hora de ingreso de la visita (RF-17).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega las columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns, lista AppData.ReceiptBalanceColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   recibo.saldo_pendiente:        parte del total que quedó sin pagar al terminar la visita (0 = pagado completo).
--                                  total = lo pagado en la visita + saldo_pendiente; monto_efectivo / monto_yape guardan solo lo pagado.
--   recibo.saldo_autorizado_por:   nombre de la dueña que autorizó el saldo con su PIN (el motivo queda en Auditoría,
--                                  acción SALDO_PENDIENTE_AUTORIZADO).
--   recibo.saldo_cobrado_at:       fecha del pago posterior del saldo (NULL = aún se debe). Auditoría: SALDO_COBRADO.
--   recibo.saldo_cobrado_medio:    Efectivo, Yape o Mixto.
--   recibo.saldo_cobrado_efectivo / saldo_cobrado_yape: cómo se pagó el saldo. El efectivo entra a la caja del turno en que se cobra.
--   recibo.saldo_cobrado_por:      usuario que cobró el saldo.
--   recibo.saldo_id_sesion:        turno de caja abierto cuando se cobró el saldo.
--   recibo.ingreso_at:             hora de creación del ticket guardado que se cobró (duración de la visita = created_at - ingreso_at).

USE edenpos;

ALTER TABLE recibo ADD COLUMN saldo_pendiente DECIMAL(10,2) NOT NULL DEFAULT 0;
ALTER TABLE recibo ADD COLUMN saldo_autorizado_por VARCHAR(60) NULL;
ALTER TABLE recibo ADD COLUMN saldo_cobrado_at DATETIME NULL;
ALTER TABLE recibo ADD COLUMN saldo_cobrado_medio VARCHAR(10) NULL;
ALTER TABLE recibo ADD COLUMN saldo_cobrado_efectivo DECIMAL(10,2) NOT NULL DEFAULT 0;
ALTER TABLE recibo ADD COLUMN saldo_cobrado_yape DECIMAL(10,2) NOT NULL DEFAULT 0;
ALTER TABLE recibo ADD COLUMN saldo_cobrado_por VARCHAR(60) NULL;
ALTER TABLE recibo ADD COLUMN saldo_id_sesion CHAR(32) NULL;
ALTER TABLE recibo ADD COLUMN ingreso_at DATETIME NULL;
