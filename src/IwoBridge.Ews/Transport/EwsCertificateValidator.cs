using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using IwoBridge.Core.Transport;

namespace IwoBridge.Ews.Transport
{
    /// <summary>
    /// Проверка сертификата сервера EWS поверх чистой <see cref="CertificatePinningPolicy"/>.
    ///
    /// Один валидатор на подключение; вызывается через общий процессный обработчик <see cref="TlsTrustRegistry"/> по
    /// группе соединений подключения. Дополнительно сверяются хост и порт EWS: прочие соединения проверяются
    /// стандартно (принимаются только без ошибок политики).
    ///
    /// TOFU: при первом доверенном подключении отпечаток SHA-256 атомарно закрепляется в памяти, и все
    /// последующие соединения сеанса сверяются с ним — в том числе параллельные. Сохранить закрепление в
    /// конфигурацию приложение может после подтверждения пользователем через <see cref="PinnedThumbprint"/>;
    /// из потоков ввода-вывода пользовательский код не вызывается.
    ///
    /// Для решения пользователя валидатор запоминает последний предъявленный сервером EWS сертификат
    /// (<see cref="LastPresented"/>) — в том числе отклонённый: окно покажет, кому и кем он выдан, и его отпечаток.
    /// Класс потокобезопасен.
    /// </summary>
    public sealed class EwsCertificateValidator
    {
        private const int NoDecision = -1;

        private readonly string _host;
        private readonly int _port;
        private string? _pinned;
        private int _lastDecision = NoDecision;
        private PresentedCertificate? _lastPresented;

        public EwsCertificateValidator(Uri ewsUrl, string? pinnedThumbprint)
        {
            if (ewsUrl is null)
            {
                throw new ArgumentNullException(nameof(ewsUrl));
            }

            if (!ewsUrl.IsAbsoluteUri)
            {
                throw new ArgumentException("Адрес EWS должен быть абсолютным URI.", nameof(ewsUrl));
            }

            _host = ewsUrl.Host;
            _port = ewsUrl.Port;
            try
            {
                _pinned = Thumbprint.Normalize(pinnedThumbprint);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message, nameof(pinnedThumbprint), ex);
            }
        }

        /// <summary>Закреплённый отпечаток: заданный в конфигурации либо захваченный SHA-256 после TOFU.</summary>
        public string? PinnedThumbprint => Volatile.Read(ref _pinned);

        /// <summary>Последнее решение по соединению с EWS — для понятного сообщения при отказе.</summary>
        public CertificateDecision? LastDecision
        {
            get
            {
                int value = Volatile.Read(ref _lastDecision);
                return value == NoDecision ? (CertificateDecision?)null : (CertificateDecision)value;
            }
        }

        /// <summary>Последний сертификат, предъявленный сервером EWS (принятый или отклонённый); иначе <c>null</c>.</summary>
        public PresentedCertificate? LastPresented => Volatile.Read(ref _lastPresented);

        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        {
            if (!IsEwsEndpoint(sender))
            {
                return sslPolicyErrors == SslPolicyErrors.None;
            }

            if (certificate is null)
            {
                Record(CertificateDecision.RejectUntrusted);
                return false;
            }

            try
            {
                PresentedCertificate presented = Describe(certificate);
                Volatile.Write(ref _lastPresented, presented);
                bool chainTrusted =
                    (sslPolicyErrors & (SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0;

                CertificateDecision decision = CertificatePinningPolicy.Evaluate(
                    presented.Fingerprint, Volatile.Read(ref _pinned), presented.Issuer, chainTrusted);

                if (decision == CertificateDecision.AcceptFirstUse)
                {
                    string? previous = Interlocked.CompareExchange(ref _pinned, presented.Fingerprint.Sha256, null);
                    if (previous != null)
                    {
                        // Параллельное соединение успело закрепить свой сертификат — сверяемся с ним.
                        decision = CertificatePinningPolicy.Evaluate(presented.Fingerprint, previous, presented.Issuer, chainTrusted);
                    }
                }

                Record(decision);
                return CertificatePinningPolicy.IsAccepted(decision);
            }
#pragma warning disable CA1031 // Обратный вызов TLS не должен выбрасывать: любая ошибка проверки означает отказ.
            catch (Exception)
#pragma warning restore CA1031
            {
                Record(CertificateDecision.RejectUntrusted);
                return false;
            }
        }

        private static PresentedCertificate Describe(X509Certificate certificate)
        {
            using (var cert = new X509Certificate2(certificate))
            using (SHA256 sha256 = SHA256.Create())
            {
                var fingerprint = new CertificateFingerprint(cert.Thumbprint, Thumbprint.ToHex(sha256.ComputeHash(cert.RawData)));
                return new PresentedCertificate(
                    cert.Subject, cert.Issuer, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), fingerprint);
            }
        }

        /// <summary>
        /// Соединение именно с хостом и портом EWS. EWS Managed API всегда передаёт <see cref="HttpWebRequest"/>; прочие
        /// отправители (например, <c>SslStream</c> с именем хоста) сюда не относятся и проверяются стандартно.
        /// </summary>
        private bool IsEwsEndpoint(object sender)
        {
            return sender is HttpWebRequest request
                && string.Equals(request.Address.Host, _host, StringComparison.OrdinalIgnoreCase)
                && request.Address.Port == _port;
        }

        private void Record(CertificateDecision decision)
        {
            Volatile.Write(ref _lastDecision, (int)decision);
        }
    }
}
