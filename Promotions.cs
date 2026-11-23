using System;
using System.Collections.Generic;
using System.Linq;

namespace EdenPOS
{
    // Datos que necesita una promoción para calcularse. Los llena AppData (con SQL, dentro de la transacción del cobro
    // o en una conexión propia para la vista previa del diálogo); las promociones en sí no tocan la base de datos.
    public sealed class PromocionContexto
    {
        public string ClienteDni { get; set; }
        public List<ReceiptLine> Lineas { get; set; } = new List<ReceiptLine>();
        // Ids de producto cuya categoría es SAUNA (las variantes heredan la categoría de su producto).
        public HashSet<string> ProductosSauna { get; set; } = new HashSet<string>();
        // Historial del DNI: recibos Pagado con al menos una línea SAUNA, separados en cobrados y exonerados (SEXTA_VISITA).
        public int VisitasPagadas { get; set; }
        public int VisitasGratis { get; set; }
        public bool SextaVisitaActiva { get; set; }
        public int GrupalMinimoPersonas { get; set; }
        public decimal GrupalPorcentaje { get; set; }

        public decimal Total { get { return Math.Round(Lineas.Sum(x => x.LineTotal), 2); } }

        public bool EsSauna(ReceiptLine linea)
        {
            return linea != null && !string.IsNullOrEmpty(linea.ProductId) && ProductosSauna.Contains(linea.ProductId);
        }
    }

    public sealed class PromocionResultado
    {
        public static readonly PromocionResultado Ninguna = new PromocionResultado { Descuento = 0m, PromocionId = null, Descripcion = "" };

        public decimal Descuento { get; set; }
        // "SEXTA_VISITA", "GRUPAL" o null (sin promoción).
        public string PromocionId { get; set; }
        // Texto para la UI, por ejemplo "6ª visita gratis".
        public string Descripcion { get; set; }
        public bool Aplica { get { return Descuento > 0m && !string.IsNullOrEmpty(PromocionId); } }
    }

    // Strategy: cada promoción calcula su propio descuento sobre el mismo contexto.
    /// <summary>
    /// Base de las promociones (patrón Strategy). Cada promoción concreta calcula su descuento
    /// sobre el mismo contexto; en el cobro se aplica solo la de mayor beneficio (RN-05).
    /// </summary>
    public abstract class Promocion
    {
        /// <summary>Código estable que se guarda en el recibo (id_promocion_aplicada).</summary>
        public abstract string Id { get; }

        /// <summary>Descuento en soles que corresponde a este contexto; 0 si no aplica.</summary>
        public abstract decimal CalcularDescuento(PromocionContexto contexto);

        /// <summary>Texto corto que se muestra a la cajera en el diálogo de cobro.</summary>
        public abstract string Describir(PromocionContexto contexto);
    }

    // RN-03. Ciclo: 5 visitas pagadas y la 6ª gratis. Una "visita" es un recibo Pagado del mismo DNI con al menos una línea
    // de categoría SAUNA. El recibo exonerado (id_promocion_aplicada = 'SEXTA_VISITA') NO cuenta como visita pagada: consume
    // 5 de las visitas pagadas acumuladas. La visita actual es gratis si VisitasPagadas - 5 * VisitasGratis >= 5, así el ciclo es
    // 5 pagadas + 1 gratis (6ª, 12ª, 18ª...) y la 7ª ya se cobra normal. Si la promoción estuvo desactivada, el crédito acumulado
    // (>= 5) se aplica en la siguiente visita. Un recibo reembolsado no cuenta (ni pagado ni gratis). Exonera una unidad de la
    // línea SAUNA de mayor precio unitario; los consumos y el resto de líneas se cobran. Sin DNI válido no califica.
    public sealed class SextaVisitaGratis : Promocion
    {
        public const string Codigo = "SEXTA_VISITA";
        public const int VisitasPorCiclo = 5;

        public override string Id { get { return Codigo; } }

        public override decimal CalcularDescuento(PromocionContexto contexto)
        {
            if (!contexto.SextaVisitaActiva || !Validation.IsValidDni(contexto.ClienteDni)) return 0m;
            if (contexto.VisitasPagadas - VisitasPorCiclo * contexto.VisitasGratis < VisitasPorCiclo) return 0m;
            var precios = contexto.Lineas.Where(x => contexto.EsSauna(x) && x.Quantity > 0 && x.UnitPrice > 0m).Select(x => x.UnitPrice).ToList();
            return precios.Count == 0 ? 0m : Math.Round(precios.Max(), 2, MidpointRounding.AwayFromZero);
        }

        public override string Describir(PromocionContexto contexto)
        {
            return "6ª visita gratis";
        }
    }

    // RN-04. Personas = adultos + niños inferidos de las líneas con OccupancyCalculator; si alcanzan el mínimo configurado,
    // el porcentaje configurado se descuenta del total (redondeo a 2 decimales, mitad hacia arriba).
    public sealed class DescuentoGrupal : Promocion
    {
        public const string Codigo = "GRUPAL";

        public override string Id { get { return Codigo; } }

        public static int ContarPersonas(IEnumerable<ReceiptLine> lineas)
        {
            int adultos;
            int ninos;
            OccupancyCalculator.Count(lineas, out adultos, out ninos);
            return adultos + ninos;
        }

        public override decimal CalcularDescuento(PromocionContexto contexto)
        {
            if (contexto.GrupalPorcentaje <= 0m || contexto.GrupalMinimoPersonas < 1) return 0m;
            if (ContarPersonas(contexto.Lineas) < contexto.GrupalMinimoPersonas) return 0m;
            return Math.Round(contexto.Total * contexto.GrupalPorcentaje / 100m, 2, MidpointRounding.AwayFromZero);
        }

        public override string Describir(PromocionContexto contexto)
        {
            return "Descuento grupal " + contexto.GrupalPorcentaje.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   "% (" + ContarPersonas(contexto.Lineas) + " personas)";
        }
    }

    public static class PromocionCalculadora
    {
        // Lista fija de estrategias; en empate gana la primera (sexta visita).
        private static readonly Promocion[] Promociones = { new SextaVisitaGratis(), new DescuentoGrupal() };

        // RN-05: no se acumulan. Se calculan todas y se aplica la de mayor descuento (nunca más que el total).
        public static PromocionResultado Evaluar(PromocionContexto contexto)
        {
            if (contexto == null) throw new ArgumentNullException("contexto");
            PromocionResultado mejor = PromocionResultado.Ninguna;
            var total = contexto.Total;
            foreach (var promocion in Promociones)
            {
                var descuento = Math.Min(promocion.CalcularDescuento(contexto), total);
                if (descuento > mejor.Descuento)
                    mejor = new PromocionResultado { Descuento = descuento, PromocionId = promocion.Id, Descripcion = promocion.Describir(contexto) };
            }
            return mejor;
        }

        // Lee el historial y la configuración de MySQL (conexión propia) y evalúa. Es la vista previa del diálogo de cobro;
        // AddReceipt vuelve a calcularla dentro de su transacción.
        public static PromocionResultado Calcular(string clienteDni, List<ReceiptLine> lineas)
        {
            return AppData.CalculatePromotion(clienteDni, lineas);
        }

        public static void Calcular(string clienteDni, List<ReceiptLine> lineas, out decimal descuento, out string promocionAplicada)
        {
            var resultado = Calcular(clienteDni, lineas);
            descuento = resultado.Descuento;
            promocionAplicada = resultado.PromocionId;
        }
    }
}
