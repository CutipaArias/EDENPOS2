-- 009: tipo de artículo (RF-14), retiro del consentimiento del cliente (RNF-09 / RN-14) y último mensaje del emisor simulado (RF-21).
--
-- NO hace falta ejecutar este archivo: Edén POS agrega las columnas solo al iniciar (AppData.Initialize ->
-- EnsureUserLockColumns), consultando information_schema y sin tocar nada si ya existen.
-- Está aquí para quien prefiera aplicarlo a mano (phpMyAdmin, mysql.exe) antes de abrir la app.
--
-- Compatible con MariaDB 10.4 y MySQL 8.4. Ejecútelo UNA sola vez (una segunda ejecución falla con "Duplicate column name").
--
--   producto.tipo:                    'Producto' (se vende o se consume) o 'Implemento' (se presta y se devuelve: toalla, sandalia...).
--                                     Solo los implementos se revisan al finalizar la visita y tienen cargo por pérdida (RN-07).
--                                     Se eligió VARCHAR y no un TINYINT es_implemento: se lee igual en la base, las copias y Supabase,
--                                     y admite otro tipo sin otra migración.
--                                     Los artículos que ya eran implementos con la regla anterior (categoría ALQUILERES o
--                                     cargo_perdida > 0) pasan a 'Implemento' (UPDATE de abajo).
--   cliente.consentimiento_revocado:  momento en que el cliente retiró su consentimiento (NULL = no lo retiró). Al retirarlo se borra
--                                     el teléfono y se anonimiza el DNI (ver CustomerPrivacy.cs).
--   comprobante.mensaje_respuesta:    último mensaje del emisor SIMULADO (no se envía nada a SUNAT); NULL si aún no se envió.

USE edenpos;

ALTER TABLE producto ADD COLUMN tipo VARCHAR(12) NOT NULL DEFAULT 'Producto';
UPDATE producto SET tipo = 'Implemento' WHERE cargo_perdida > 0 OR UPPER(TRIM(categoria)) = 'ALQUILERES';

ALTER TABLE cliente ADD COLUMN consentimiento_revocado DATETIME NULL;

ALTER TABLE comprobante ADD COLUMN mensaje_respuesta VARCHAR(200) NULL;
