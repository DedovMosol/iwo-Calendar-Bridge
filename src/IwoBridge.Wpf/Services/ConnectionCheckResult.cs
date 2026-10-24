using IwoBridge.Core.Transport;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Итог «Проверить подключение»: удалось ли, сколько элементов в календаре, какой сертификат предъявил сервер и
    /// какое решение по нему принято. Сертификат приходит и при отказе — окно покажет его для решения «доверять».
    /// </summary>
    public sealed class ConnectionCheckResult
    {
        private ConnectionCheckResult(
            bool succeeded,
            int itemCount,
            UserMessage? error,
            CertificateDecision? decision,
            PresentedCertificate? certificate,
            string? pinnedThumbprint)
        {
            Succeeded = succeeded;
            ItemCount = itemCount;
            Error = error;
            Decision = decision;
            Certificate = certificate;
            PinnedThumbprint = pinnedThumbprint;
        }

        public bool Succeeded { get; }

        /// <summary>Элементов в основном календаре целевого ящика (при успехе).</summary>
        public int ItemCount { get; }

        public UserMessage? Error { get; }

        /// <summary>Решение по сертификату; <c>null</c> — до TLS дело не дошло (например, сервер недоступен).</summary>
        public CertificateDecision? Decision { get; }

        public PresentedCertificate? Certificate { get; }

        /// <summary>Отпечаток, с которым прошло подключение (из настроек или принятый при первом подключении).</summary>
        public string? PinnedThumbprint { get; }

        public static ConnectionCheckResult Success(int itemCount, CertificateDecision? decision, PresentedCertificate? certificate, string? pinned)
        {
            return new ConnectionCheckResult(true, itemCount, null, decision, certificate, pinned);
        }

        public static ConnectionCheckResult Failure(UserMessage error, CertificateDecision? decision, PresentedCertificate? certificate)
        {
            return new ConnectionCheckResult(false, 0, error, decision, certificate, null);
        }
    }
}
