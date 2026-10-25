using System;
using System.Text.RegularExpressions;

namespace IwoBridge.Core.Transport
{
    /// <summary>
    /// Чистая политика проверки сертификата сервера. Принимает уже извлечённые адаптером
    /// примитивы (отпечатки, издатель, доверенность цепочки) — без типов X509, поэтому полностью покрывается
    /// модульными тестами. <c>chainTrusted</c> — Windows доверяет сертификату для этого адреса (цепочка и имя): только
    /// такой сертификат закрепляется при первом подключении без вопроса. Закреплённый отпечаток проверяется без учёта
    /// имени: сертификат выписан на имя сервера, а подключаются часто по IP, и доверие уже дал человек.
    /// </summary>
    public static class CertificatePinningPolicy
    {
        // Маркеры продуктов, перехватывающих HTTPS (пересоздают сертификат своим корневым ЦС). Совпадение
        // ищется по границам «буква/не буква»: «ESET» не совпадёт с «Reset», а «Fortinet_CA_SSL» совпадёт с
        // «Fortinet». Это диагностика для понятного сообщения, а не данные организации.
        private static readonly Regex InterceptorPattern = new Regex(
            @"(?<!\p{L})(Kaspersky|Dr\.?\s?Web|ESET|Avast|AVG|Fortinet|FortiGate|Zscaler|Bitdefender|Sophos|Netskope|Symantec\s+Web|Kerio)(?!\p{L})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static CertificateDecision Evaluate(
            CertificateFingerprint presented, string? pinnedThumbprint, string? issuerName, bool chainTrusted)
        {
            if (presented is null)
            {
                throw new ArgumentNullException(nameof(presented));
            }

            string? pinned = Thumbprint.Normalize(pinnedThumbprint);
            bool interceptor = LooksLikeInterceptor(issuerName);

            if (pinned != null)
            {
                if (presented.Matches(pinned))
                {
                    return CertificateDecision.AcceptPinned;
                }

                return interceptor ? CertificateDecision.RejectInterception : CertificateDecision.RejectPinMismatch;
            }

            if (interceptor)
            {
                return CertificateDecision.RejectInterception;
            }

            return chainTrusted ? CertificateDecision.AcceptFirstUse : CertificateDecision.RejectUntrusted;
        }

        public static bool IsAccepted(CertificateDecision decision)
        {
            return decision == CertificateDecision.AcceptPinned || decision == CertificateDecision.AcceptFirstUse;
        }

        private static bool LooksLikeInterceptor(string? issuerName)
        {
            return !string.IsNullOrEmpty(issuerName) && InterceptorPattern.IsMatch(issuerName);
        }
    }
}
