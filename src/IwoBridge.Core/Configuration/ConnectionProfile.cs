using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Configuration
{
    /// <summary>
    /// Профиль подключения к приёмнику. Содержит только данные конкретной установки и целиком приходит
    /// из пользовательской конфигурации — в коде, документах, тестах и логах таких значений нет (T36).
    /// Секреты (пароль) сюда не входят: они хранятся в диспетчере учётных данных Windows и читаются
    /// отдельно (PLAN.md §9, §11).
    ///
    /// Неверное значение даёт <see cref="ArgumentException"/>, у которого <see cref="ArgumentException.ParamName"/>
    /// равен имени параметра конструктора; имена параметров совпадают с полями файла настроек, поэтому
    /// загрузчик профиля указывает пользователю именно то поле, которое надо исправить.
    /// </summary>
    public sealed class ConnectionProfile
    {
        /// <param name="alternateEwsUrl">
        /// Необязательный резервный адрес того же сервера (например, внешний при внутреннем основном). Подключение
        /// переходит на него, только когда основной недоступен по сети (<see cref="Errors.ErrorClassifier.IsUnreachable"/>).
        /// </param>
        /// <param name="alternatePinnedCertificateThumbprint">Закреплённый сертификат резервного адреса — свой.</param>
        public ConnectionProfile(
            Uri ewsUrl,
            string? mailboxAddress,
            TimeSpan utcOffset,
            string? pinnedCertificateThumbprint,
            Uri? alternateEwsUrl = null,
            string? alternatePinnedCertificateThumbprint = null)
        {
            if (ewsUrl is null)
            {
                throw new ArgumentNullException(nameof(ewsUrl));
            }

            ValidateUrl(ewsUrl, nameof(ewsUrl));
            EwsUrl = ewsUrl;
            MailboxAddress = NormalizeMailbox(mailboxAddress);
            TimeZone = TargetTimeZone.FromOffset(utcOffset);
            PinnedCertificateThumbprint = NormalizeThumbprint(pinnedCertificateThumbprint, nameof(pinnedCertificateThumbprint));

            if (alternateEwsUrl is null)
            {
                // Отпечаток без адреса — недописанные настройки: молча выбросить его значило бы потерять доверие.
                if (!string.IsNullOrWhiteSpace(alternatePinnedCertificateThumbprint))
                {
                    throw new ArgumentException(
                        "Отпечаток резервного сервера задан, а адрес резервного сервера — нет.", nameof(alternatePinnedCertificateThumbprint));
                }
            }
            else
            {
                ValidateUrl(alternateEwsUrl, nameof(alternateEwsUrl));
                if (ServerEndpoint.KeyOf(alternateEwsUrl) == ServerEndpoint.KeyOf(ewsUrl))
                {
                    throw new ArgumentException(
                        "Резервный адрес совпадает с основным (тот же сервер и порт) — от такого резерва нет пользы.",
                        nameof(alternateEwsUrl));
                }

                AlternateEwsUrl = alternateEwsUrl;
                AlternatePinnedCertificateThumbprint = NormalizeThumbprint(
                    alternatePinnedCertificateThumbprint, nameof(alternatePinnedCertificateThumbprint));
            }

            var primary = new ServerEndpoint(EwsUrl, PinnedCertificateThumbprint, isAlternate: false);
            Endpoints = alternateEwsUrl is null
                ? new[] { primary }
                : new[] { primary, new ServerEndpoint(alternateEwsUrl, AlternatePinnedCertificateThumbprint, isAlternate: true) };
        }

        /// <summary>Адрес EWS, например <c>https://&lt;host&gt;/EWS/Exchange.asmx</c>.</summary>
        public Uri EwsUrl { get; }

        /// <summary>
        /// SMTP-адрес целевого ящика. <c>null</c> — ящик самой учётной записи, под которой идёт вход.
        /// </summary>
        public string? MailboxAddress { get; }

        /// <summary>Местный пояс приёмника (фиксированное смещение, без DST).</summary>
        public TargetTimeZone TimeZone { get; }

        /// <summary>
        /// Отпечаток закреплённого (pinned) сертификата сервера в верхнем регистре без разделителей.
        /// <c>null</c> — закрепление не задано (берётся при первом подключении).
        /// </summary>
        public string? PinnedCertificateThumbprint { get; }

        /// <summary>Резервный адрес EWS; <c>null</c> — не задан.</summary>
        public Uri? AlternateEwsUrl { get; }

        /// <summary>Закреплённый отпечаток сертификата резервного адреса (только вместе с адресом).</summary>
        public string? AlternatePinnedCertificateThumbprint { get; }

        /// <summary>Адреса в порядке попытки: основной, затем резервный (если задан).</summary>
        public System.Collections.Generic.IReadOnlyList<ServerEndpoint> Endpoints { get; }

        /// <summary>
        /// Профиль только с одним адресом — для проверки каждого адреса отдельно (окно «Подключение» проверяет оба и
        /// показывает сертификат каждого). Ящик и пояс — те же.
        /// </summary>
        public ConnectionProfile ForEndpoint(ServerEndpoint endpoint)
        {
            if (endpoint is null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            return new ConnectionProfile(endpoint.EwsUrl, MailboxAddress, TimeZone.Offset, endpoint.PinnedCertificateThumbprint);
        }

        // Единое правило адреса — то же, что у поля «Сервер» (DRY): https, без имени и пароля в адресе, порт не 0.
        private static void ValidateUrl(Uri url, string field)
        {
            string? problem = ServerAddress.Validate(url);
            if (problem != null)
            {
                throw new ArgumentException(problem, field);
            }
        }

        /// <summary>
        /// Пустое значение — свой ящик. Иначе — простая проверка формы SMTP-адреса: ровно один «@» с непустыми
        /// частями, без пробелов и угловых скобок. Полную проверку делает сервер; здесь ловятся опечатки и
        /// вставка «Имя &lt;адрес&gt;», чтобы ошибка была понятной ещё до подключения.
        /// </summary>
        private static string? NormalizeMailbox(string? mailboxAddress)
        {
            if (mailboxAddress is null)
            {
                return null;
            }

            string trimmed = mailboxAddress.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            int at = trimmed.IndexOf('@');
            bool shapeOk = at > 0 && at < trimmed.Length - 1 && trimmed.IndexOf('@', at + 1) < 0;
            // Невидимые символы форматирования (U+200E и т. п.) приходят при копировании из окон Windows — адрес с ними
            // сервер не найдёт, а человек их не увидит. Запятая и точка с запятой — признак нескольких адресов.
            foreach (char c in trimmed)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '<' || c == '>' || c == ',' || c == ';'
                    || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                {
                    shapeOk = false;
                    break;
                }
            }

            if (!shapeOk)
            {
                throw new ArgumentException(
                    "Адрес ящика должен быть SMTP-адресом вида имя@домен.", nameof(mailboxAddress));
            }

            return trimmed;
        }

        private static string? NormalizeThumbprint(string? thumbprint, string field)
        {
            try
            {
                return Transport.Thumbprint.Normalize(thumbprint);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(ex.Message, field, ex);
            }
        }
    }
}
