using System;

namespace IwoBridge.Core.Transport
{
    /// <summary>
    /// Отпечатки предъявленного сертификата: SHA-1 (так его показывает Windows) и SHA-256. Закрепление
    /// сравнивается по длине закреплённого значения: 64 символа — SHA-256 (рекомендуемый вариант, по нему
    /// же выполняется TOFU-захват), 40 символов — SHA-1 (совместимость с отпечатком из интерфейса Windows).
    /// </summary>
    public sealed class CertificateFingerprint
    {
        public CertificateFingerprint(string sha1Hex, string sha256Hex)
        {
            Sha1 = Require(sha1Hex, Thumbprint.Sha1HexLength, nameof(sha1Hex));
            Sha256 = Require(sha256Hex, Thumbprint.Sha256HexLength, nameof(sha256Hex));
        }

        public string Sha1 { get; }

        public string Sha256 { get; }

        /// <summary>Совпадает ли сертификат с закреплённым отпечатком (SHA-1 или SHA-256, по длине).</summary>
        public bool Matches(string? pinnedThumbprint)
        {
            string? pin = Thumbprint.Normalize(pinnedThumbprint);
            if (pin is null)
            {
                return false;
            }

            string own = pin.Length == Thumbprint.Sha256HexLength ? Sha256 : Sha1;
            return string.Equals(pin, own, StringComparison.Ordinal);
        }

        private static string Require(string value, int expectedLength, string parameterName)
        {
            string? normalized;
            try
            {
                normalized = Thumbprint.Normalize(value);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message, parameterName, ex);
            }

            if (normalized is null || normalized.Length != expectedLength)
            {
                throw new ArgumentException($"Ожидается {expectedLength} шестнадцатеричных символов.", parameterName);
            }

            return normalized;
        }
    }
}
