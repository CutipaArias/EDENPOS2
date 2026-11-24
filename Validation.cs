using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace EdenPOS
{
    public static class Validation
    {
        public static string NormalizeDni(string value)
        {
            return new string((value ?? "").Where(char.IsDigit).Take(8).ToArray());
        }

        public static bool IsValidDni(string value)
        {
            var normalized = NormalizeDni(value);
            return normalized.Length == 8 && normalized.Any(x => x != '0');
        }

        // RF-06: teléfono del cliente. Se aceptan espacios, guiones y el prefijo +51; se guarda solo el celular de 9 dígitos.
        public static string NormalizePhone(string value)
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length == 11 && digits.StartsWith("51", StringComparison.Ordinal)) digits = digits.Substring(2);
            return digits;
        }

        // Vacío (el teléfono es opcional) o un celular peruano: 9 dígitos que empiezan en 9.
        public static bool IsValidPhone(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return Regex.IsMatch(NormalizePhone(value), @"^9\d{8}$");
        }

        public static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
        }

        public static bool IsValidPersonName(string value)
        {
            var normalized = NormalizeName(value);
            return normalized.Length >= 3 && normalized.Length <= 120 && normalized.Any(char.IsLetter);
        }

        public static bool IsValidRequiredText(string value, int minimum = 3, int maximum = 160)
        {
            var text = (value ?? "").Trim();
            return text.Length >= minimum && text.Length <= maximum;
        }

        public static bool IsValidMoney(decimal value, bool allowZero = true)
        {
            return value >= (allowZero ? 0m : 0.01m) && value <= 1000000m;
        }
    }
}
