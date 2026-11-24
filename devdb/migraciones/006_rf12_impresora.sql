-- 006: impresora térmica de recibos por PC (RF-12 / RNF-07).
--
-- NO hace falta ejecutar este archivo: Edén POS crea la tabla solo al iniciar (AppData.Initialize -> EnsurePrinterTable,
-- en ReceiptPrinting.cs) con CREATE TABLE IF NOT EXISTS, sin tocar nada si ya existe.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Se puede ejecutar más de una vez.
--
--   equipo:             nombre de la PC (Environment.MachineName). Cada caja guarda su propia impresora.
--   nombre_impresora:   nombre de la impresora en Windows (NULL = sin impresora configurada).
--   imprimir_al_cobrar: 1 = imprimir el recibo automáticamente al cobrar en Ventas.
--
-- Es configuración propia de cada PC: no se incluye en las copias .edenbackup ni en la réplica de Supabase.

USE edenpos;

CREATE TABLE IF NOT EXISTS impresora_pc (
  equipo VARCHAR(64) PRIMARY KEY,
  nombre_impresora VARCHAR(200) NULL,
  imprimir_al_cobrar TINYINT(1) NOT NULL DEFAULT 0
);
