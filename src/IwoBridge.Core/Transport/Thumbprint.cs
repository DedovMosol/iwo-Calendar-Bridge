using System;
using System.Globalization;
using System.Text;

namespace IwoBridge.Core.Transport
{
    /// <summary>
    /// Нормализация и проверка отпечатка сертификата — одно место на продукт (DRY).
    ///
    /// Удаляет пробелы, разделители (<c>:</c>, <c>-</c>) и невидимые символы форматирования Unicode, например
    /// U+200E, который подставляет диалог сертификатов Windows при копировании отпечатка. Иначе
    /// закреплённый отпечаток никогда не совпал бы с предъявленным. Допустимы только SHA-1 (40 hex) и
    /// SHA-256 (64 hex); остальное — <see cref="FormatException"/>, чтобы опечатка в конфигурации не
    /// проходила молча.
    /// </summary>
    public static class Thumbprint
    {
        public const int Sha1HexLength = 40;
        public const int Sha256HexLength = 64;

        public static string? Normalize(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == ':' || c == '-'
                    || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(c));
            }

            if (builder.Length == 0)
            {
                return null;
            }

            string normalized = builder.ToString();
            if ((normalized.Length != Sha1HexLength && normalized.Length != Sha256HexLength) || !IsHex(normalized))
            {
                throw new FormatException(
                    "Отпечаток сертификата должен содержать 40 (SHA-1) или 64 (SHA-256) шестнадцатеричных символа.");
            }

            return normalized;
        }

        /// <summary>Байты → шестнадцатеричная строка в верхнем регистре без разделителей.</summary>
        public static string ToHex(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
