using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace EdenPOS
{
    public sealed class Product
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public decimal Price { get; set; }
        public string ImageFile { get; set; }
        public string ColorHex { get; set; }
        public bool Active { get; set; }
        public int Stock { get; set; }
        public int InitialStock { get; set; }
        public bool TrackInventory { get; set; }
        public int StockMinimo { get; set; }
        // RN-07 / RF-22: cargo que se suma al recibo si el cliente no devuelve este implemento (0 = sin cargo). Las variantes usan el del artículo.
        public decimal CargoPerdida { get; set; }
        public List<ProductOption> Options { get; set; }
    }

    // Nombres de acción de la tabla auditoria (cortos y estables: la pantalla de auditoría y los filtros dependen de ellos).
    public static class AuditActions
    {
        public const string ProductoEliminado = "PRODUCTO_ELIMINADO";
        public const string ReciboReembolsado = "RECIBO_REEMBOLSADO";
        public const string CajaDiferencia = "CAJA_DIFERENCIA";
        public const string UsuarioEliminado = "USUARIO_ELIMINADO";
        public const string UsuarioDesactivado = "USUARIO_DESACTIVADO";
        public const string PrecioCambiado = "PRECIO_CAMBIADO";
        public const string CopiaRestaurada = "COPIA_RESTAURADA";
        public const string DatosImportados = "DATOS_IMPORTADOS";
        public const string ComprobanteRechazado = "COMPROBANTE_RECHAZADO";
        public const string CuentaBloqueada = "CUENTA_BLOQUEADA";
        public const string CuentaDesbloqueada = "CUENTA_DESBLOQUEADA";
        public const string SesionInactividad = "SESION_INACTIVIDAD";
        public const string InventarioAjustado = "INVENTARIO_AJUSTADO";
        // RF-19 / RN-06 / RN-07: llave o implemento no devuelto al cobrar la visita, y liberación de una llave repuesta.
        public const string LlavePerdida = "LLAVE_PERDIDA";
        public const string ImplementoPerdido = "IMPLEMENTO_PERDIDO";
        public const string LlaveLiberada = "LLAVE_LIBERADA";
        public const string CargoPerdidaCambiado = "CARGO_PERDIDA_CAMBIADO";
        public const string LlavesQuitadas = "LLAVES_QUITADAS";
    }

    // RF-19: resultado de la devolución de llaves e implementos al cobrar una visita (ticket guardado con llaves).
    // LostKeys + ReturnedKeys deben ser EXACTAMENTE las llaves del ticket (AddReceipt lo comprueba con las filas bloqueadas): así una llave que
    // otra caja agregó mientras el diálogo estaba abierto no se libera sin revisarla. AppData.BuildLossChargeLines arma los cargos de lo no devuelto.
    public sealed class LossReport
    {
        public List<int> LostKeys { get; set; } = new List<int>();
        public List<int> ReturnedKeys { get; set; } = new List<int>();
        public List<LostImplement> LostImplements { get; set; } = new List<LostImplement>();

        [ScriptIgnore]
        public bool IsEmpty { get { return (LostKeys == null || LostKeys.Count == 0) && (LostImplements == null || LostImplements.Count == 0); } }
    }

    public sealed class LostImplement
    {
        public string ProductId { get; set; }
        public string OptionId { get; set; }
        public int Quantity { get; set; }
    }

    // RF-15: tipos de movimiento de inventario (columna movimiento_inventario.tipo).
    // Entrada, Venta, Reembolso, Pérdida y Consumo interno guardan la cantidad en positivo; Ajuste guarda la diferencia con signo
    // (stock contado - stock del sistema).
    public static class InventoryMovementTypes
    {
        public const string Entrada = "Entrada";
        public const string Venta = "Venta";
        public const string Reembolso = "Reembolso";
        public const string Perdida = "Pérdida";
        public const string ConsumoInterno = "Consumo interno";
        public const string Ajuste = "Ajuste";
    }

    public sealed class AuditEntry
    {
        public DateTime Date { get; set; }
        public string User { get; set; }
        public string Action { get; set; }
        public string Detail { get; set; }
    }

    public sealed class ReceiptLine
    {
        public string ProductId { get; set; }
        public string OptionId { get; set; }
        public string Name { get; set; }
        public string Comment { get; set; }
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }

        [ScriptIgnore]
        public decimal LineTotal { get { return UnitPrice * Quantity; } }
    }

    public sealed class Receipt
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Employee { get; set; }
        public string CustomerName { get; set; }
        public string CustomerDni { get; set; }
        public string PaymentMethod { get; set; }
        public decimal AmountReceived { get; set; }
        public decimal CashAmount { get; set; }
        public decimal YapeAmount { get; set; }
        public decimal Discount { get; set; }
        public string PromotionId { get; set; }
        public decimal Total { get; set; }
        public string Status { get; set; }
        public List<ReceiptLine> Lines { get; set; } = new List<ReceiptLine>();
        public DateTime? RefundedAt { get; set; }
        public string RefundReason { get; set; }

        // Comprobante electrónico SIMULADO del recibo (null en recibos importados de JSON antiguos). No se serializa.
        [ScriptIgnore]
        public Comprobante Comprobante { get; set; }

        // Suma de las líneas a precio original; Total ya trae el descuento de promoción restado.
        [ScriptIgnore]
        public decimal Subtotal { get { return Total + Discount; } }

        [ScriptIgnore]
        public decimal CashPaid { get { return PaymentMethod == "Mixto" ? CashAmount : PaymentMethod == "Efectivo" ? Total : 0m; } }

        [ScriptIgnore]
        public decimal YapePaid { get { return PaymentMethod == "Mixto" ? YapeAmount : PaymentMethod == "Yape" ? Total : 0m; } }
    }

    public sealed class SavedTicket
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CustomerName { get; set; }
        public string CustomerDni { get; set; }
        public int LockerNumber { get; set; }
        public List<int> LockerNumbers { get; set; } = new List<int>();
        public int AdultCount { get; set; }
        public int ChildCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<ReceiptLine> Lines { get; set; } = new List<ReceiptLine>();

        [ScriptIgnore]
        public decimal Total { get { return Lines == null ? 0m : Lines.Sum(x => x.LineTotal); } }
    }

    public sealed class Customer
    {
        public string Dni { get; set; }
        public string FullName { get; set; }
        public string Source { get; set; }
        // RF-06 / RNF-09: teléfono opcional y consentimiento del cliente para guardar sus datos (Ley N.º 29733).
        public string Phone { get; set; }
        public bool DataConsent { get; set; }
        public DateTime? ConsentDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class Shift
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public string Employee { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public decimal OpeningAmount { get; set; }
        public decimal ClosingAmount { get; set; }
        public decimal ExpectedAmount { get; set; }
        public bool IsOpen { get; set; }
        // RN-09: observación del cierre cuando hubo diferencia (null si cuadró).
        public string ClosingNote { get; set; }
    }

    public sealed class CashMovement
    {
        public string Id { get; set; }
        public string ShiftId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Employee { get; set; }
        public string Type { get; set; }
        public decimal Amount { get; set; }
        public string Comment { get; set; }
    }

    public sealed class InventoryMovement
    {
        public string Id { get; set; }
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Type { get; set; }
        public int Quantity { get; set; }
        public int PreviousStock { get; set; }
        public int NewStock { get; set; }
        public string Note { get; set; }
        public string ReferenceId { get; set; }
    }

    public sealed class AppUser
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Role { get; set; }
        public string PinSalt { get; set; }
        public string PinHash { get; set; }
        public bool Active { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        // RF-01: contador de PIN incorrectos y bloqueo (persisten en la base). LockedSeconds = segundos desde el bloqueo según el reloj del servidor
        // (null = sin bloqueo); así varias cajas con relojes distintos coinciden. El bloqueo efectivo lo decide UserSecurity.IsLocked.
        public int FailedAttempts { get; set; }
        public double? LockedSeconds { get; set; }
    }

    public sealed class AppSettings
    {
        public string StoreName { get; set; }
        public string EmployeeName { get; set; }
        public string CurrencySymbol { get; set; }
        public int NextReceiptNumber { get; set; }
        public int NextShiftNumber { get; set; }
        public bool DniLookupEnabled { get; set; }
        public bool PromoSextaVisitaActiva { get; set; } = true;
        public int PromoGrupalMinimoPersonas { get; set; } = 4;
        public decimal PromoGrupalPorcentaje { get; set; } = 10m;
        public string DniApiTokenProtected { get; set; }
        public string MySqlConnectionStringProtected { get; set; }
        public int InventorySchemaVersion { get; set; }
        public int CatalogSchemaVersion { get; set; }
        public int SecuritySchemaVersion { get; set; }
        public List<AppUser> Users { get; set; } = new List<AppUser>();
    }
}
