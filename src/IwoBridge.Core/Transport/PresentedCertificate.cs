using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Transport
{
    /// <summary>
    /// Сертификат, который сервер предъявил при подключении, — в виде, пригодном для показа человеку: кому и кем
    /// выдан, срок действия, отпечатки. По нему пользователь решает, доверять ли серверу (сверяет отпечаток с
    /// администратором), и он же сохраняется как закрепление. Без типов X509 — ядро остаётся без зависимостей.
    /// </summary>
    public sealed class PresentedCertificate
    {
        public PresentedCertificate(
            string subject, string issuer, DateTime notBeforeUtc, DateTime notAfterUtc, CertificateFingerprint fingerprint)
        {
            Subject = subject ?? string.Empty;
            Issuer = issuer ?? string.Empty;
            NotBeforeUtc = TimeMapper.ToUtc(notBeforeUtc);
            NotAfterUtc = TimeMapper.ToUtc(notAfterUtc);
            Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        }

        /// <summary>Кому выдан (поле Subject), например <c>CN=сервер</c>.</summary>
        public string Subject { get; }

        /// <summary>Кем выдан (поле Issuer). Совпадает с <see cref="Subject"/> у самоподписанного сертификата.</summary>
        public string Issuer { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }

        /// <summary>Отпечатки SHA-1 и SHA-256; для закрепления сохраняется SHA-256.</summary>
        public CertificateFingerprint Fingerprint { get; }

        /// <summary>Самоподписанный: выдан сам себе. Для Exchange 2007 это сертификат по умолчанию.</summary>
        public bool IsSelfSigned => string.Equals(Subject, Issuer, StringComparison.OrdinalIgnoreCase);

        /// <summary>Действует ли сертификат в указанный момент.</summary>
        public bool IsValidAt(DateTime utc)
        {
            DateTime moment = TimeMapper.ToUtc(utc);
            return moment >= NotBeforeUtc && moment <= NotAfterUtc;
        }
    }
}
