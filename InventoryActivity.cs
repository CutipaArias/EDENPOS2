using System;
using System.Collections.Generic;
using MySqlConnector;

namespace EdenPOS
{
    // Solo los contadores visibles en Inventario; no carga recibos, comprobantes ni el historial completo.
    internal sealed class InventoryActivity
    {
        internal readonly Dictionary<string, int> Entries = new Dictionary<string, int>();
        internal readonly Dictionary<string, int> Exits = new Dictionary<string, int>();
        private readonly Dictionary<Tuple<string, string>, int> optionExits = new Dictionary<Tuple<string, string>, int>();
        private readonly Dictionary<Tuple<string, string>, int> legacyExits = new Dictionary<Tuple<string, string>, int>();

        internal static int Count(Dictionary<string, int> counts, string id)
        {
            int count;
            return id != null && counts.TryGetValue(id, out count) ? count : 0;
        }

        internal int OptionExits(string productId, string optionId, string legacyName)
        {
            int current, legacy;
            optionExits.TryGetValue(Tuple.Create(productId, optionId), out current);
            legacyExits.TryGetValue(Tuple.Create(productId, legacyName), out legacy);
            return checked(current + legacy);
        }

        internal void AddEntry(string id, int quantity)
        {
            if (id != null) Entries[id] = checked(Count(Entries, id) + quantity);
        }

        internal void AddExit(string productId, string optionId, string name, int quantity)
        {
            if (productId == null) return;
            Exits[productId] = checked(Count(Exits, productId) + quantity);
            var target = string.IsNullOrWhiteSpace(optionId) ? legacyExits : optionExits;
            var key = Tuple.Create(productId, string.IsNullOrWhiteSpace(optionId) ? name : optionId);
            int count;
            target.TryGetValue(key, out count);
            target[key] = checked(count + quantity);
        }
    }

    public static partial class AppData
    {
        internal static InventoryActivity GetInventoryActivity(DateTime since)
        {
            lock (Sync)
            using (var connection = SqlConnectionFactory.Open())
            {
                var activity = new InventoryActivity();
                using (var command = new MySqlCommand(
                    "SELECT COALESCE(id_variante, id_producto), cantidad FROM movimiento_inventario " +
                    "WHERE created_at >= @desde AND BINARY tipo = 'Entrada'", connection))
                {
                    command.Parameters.AddWithValue("@desde", since);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            activity.AddEntry(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1));
                }
                using (var command = new MySqlCommand(
                    "SELECT l.id_producto, l.id_variante, l.nombre, l.cantidad FROM recibo_linea l " +
                    "INNER JOIN recibo r ON r.id_recibo = l.id_recibo " +
                    "WHERE r.created_at >= @desde AND BINARY r.estado = 'Pagado'", connection))
                {
                    command.Parameters.AddWithValue("@desde", since);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            activity.AddExit(reader.IsDBNull(0) ? null : reader.GetString(0),
                                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
                }
                return activity;
            }
        }
    }
}
