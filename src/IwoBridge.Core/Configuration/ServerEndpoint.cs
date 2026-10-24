using System;
using System.Globalization;

namespace IwoBridge.Core.Configuration
{
    /// <summary>
    /// Один адрес сервера EWS вместе с доверием к его сертификату. У профиля их один или два: основной и
    /// необязательный резервный (например, внутренний и внешний адрес одного Exchange). Закрепление сертификата
    /// у каждого адреса своё: внутренний адрес часто отвечает собственным сертификатом Exchange, а внешний —
    /// сертификатом публичного центра или прокси.
    /// </summary>
    public sealed class ServerEndpoint
    {
        internal ServerEndpoint(Uri ewsUrl, string? pinnedCertificateThumbprint, bool isAlternate)
        {
            EwsUrl = ewsUrl;
            PinnedCertificateThumbprint = pinnedCertificateThumbprint;
            IsAlternate = isAlternate;
        }

        /// <summary>Адрес EWS.</summary>
        public Uri EwsUrl { get; }

        /// <summary>Закреплённый отпечаток сертификата этого адреса; <c>null</c> — будет взят при первом подключении.</summary>
        public string? PinnedCertificateThumbprint { get; }

        /// <summary>Резервный адрес (иначе — основной).</summary>
        public bool IsAlternate { get; }

        /// <summary>Хост и порт — то, к чему относится сертификат.</summary>
        public string Key => KeyOf(EwsUrl);

        /// <summary>
        /// «хост:порт» адреса: два адреса с одним ключом — это один сервер TLS (сертификат тот же, резерв бесполезен).
        /// Хост сравнивается без учёта регистра.
        /// </summary>
        public static string KeyOf(Uri ewsUrl)
        {
            if (ewsUrl is null)
            {
                throw new ArgumentNullException(nameof(ewsUrl));
            }

            return ewsUrl.Host.ToLowerInvariant() + ":" + ewsUrl.Port.ToString(CultureInfo.InvariantCulture);
        }
    }
}
