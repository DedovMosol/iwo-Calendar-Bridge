using System;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Transport;

namespace IwoBridge.Wpf.Messages
{
    /// <summary>
    /// Тексты ошибок для пользователя — единственное место (DRY). Каждый вид ошибки ядра (<see cref="OperationErrorKind"/>)
    /// и каждое решение по сертификату (<see cref="CertificateDecision"/>) имеет заголовок «что случилось» и подсказку
    /// «что делать»; тест проверяет, что ни один вид не остался без текста.
    ///
    /// Значений из настроек (адрес, ящик, отпечаток) в текстах нет: подробности исключения передаются отдельно
    /// и показываются свёрнутыми только в окне.
    /// </summary>
    public static class ErrorMessages
    {
        public static UserMessage For(OperationErrorKind kind, string? details = null)
        {
            switch (kind)
            {
                case OperationErrorKind.Timeout:
                    return new UserMessage(
                        "Сервер не ответил вовремя.",
                        "Проверьте сеть и повторите. Если календарь очень большой, сервер может отвечать дольше обычного.",
                        details);
                case OperationErrorKind.ConnectionReset:
                    return new UserMessage(
                        "Нет связи с сервером.",
                        "Проверьте адрес сервера и подключение к сети. Внутренний адрес доступен только из сети организации, "
                        + "внешний — из интернета.",
                        details);
                case OperationErrorKind.ServerBusy:
                    return new UserMessage(
                        "Сервер перегружен и временно ограничил запросы.",
                        "Подождите несколько минут и повторите.",
                        details);
                case OperationErrorKind.ComBusy:
                    return new UserMessage(
                        "Outlook занят и не отвечает.",
                        "Закройте открытые в Outlook окна и диалоги и повторите.",
                        details);
                case OperationErrorKind.AuthenticationFailed:
                    return new UserMessage(
                        "Неверное имя пользователя или пароль.",
                        "Проверьте учётные данные в настройках подключения. Программа не повторяла попытку, чтобы не "
                        + "заблокировать учётную запись.",
                        details);
                case OperationErrorKind.VersionConflict:
                    return new UserMessage(
                        "Элемент изменился на сервере во время операции.",
                        "Повторите — программа перечитает данные.",
                        details);
                case OperationErrorKind.AccessDenied:
                    return new UserMessage(
                        "Нет прав на этот ящик.",
                        "Проверьте адрес ящика или попросите администратора выдать права на календарь.",
                        details);
                case OperationErrorKind.MailboxNotFound:
                    return new UserMessage(
                        "Ящик с таким адресом на сервере не найден.",
                        "Проверьте адрес ящика в настройках подключения или оставьте поле пустым, чтобы работать со своим ящиком.",
                        details);
                case OperationErrorKind.EndpointNotFound:
                    return new UserMessage(
                        "По этому адресу нет службы Exchange (EWS).",
                        "Проверьте адрес сервера. Обычно достаточно имени сервера — путь /EWS/Exchange.asmx программа допишет сама.",
                        details);
                case OperationErrorKind.QuotaExceeded:
                    return new UserMessage(
                        "Ящик переполнен.",
                        "Очистите папку «Удалённые» в Outlook или попросите администратора увеличить квоту.",
                        details);
                case OperationErrorKind.RequestTooLarge:
                    return new UserMessage(
                        "Сервер отклонил слишком большой запрос.",
                        "Сообщите администратору: ограничение размера запроса на сервере меньше обычного.",
                        details);
                case OperationErrorKind.SchemaError:
                    return new UserMessage(
                        "Сервер не принял запрос.",
                        "Возможно, версия сервера не поддерживается. Передайте подробности администратору.",
                        details);
                case OperationErrorKind.CertificatePinMismatch:
                    return new UserMessage(
                        "Сертификат сервера не прошёл проверку.",
                        "Откройте настройки подключения и выполните проверку — программа покажет сертификат и причину.",
                        details);
                case OperationErrorKind.TlsNegotiationFailed:
                    return new UserMessage(
                        "Не удалось установить защищённое соединение.",
                        "Сервер и Windows не договорились о версии TLS. Старым серверам нужен TLS 1.0 — он не должен быть "
                        + "отключён в Windows. Серверам, принимающим только TLS 1.2, на Windows 7 нужно включить TLS 1.2 "
                        + "в реестре (по умолчанию там включён только TLS 1.0). Обратитесь к администратору.",
                        details);
                case OperationErrorKind.Unknown:
                    return new UserMessage(
                        "Операция не выполнена.",
                        "Повторите. Если ошибка повторяется, передайте подробности администратору.",
                        details);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид ошибки.");
            }
        }

        /// <summary>Отклонённый сертификат: причина и что делать. Для принятых решений — <c>null</c>.</summary>
        public static UserMessage? ForCertificate(CertificateDecision decision)
        {
            switch (decision)
            {
                case CertificateDecision.AcceptPinned:
                case CertificateDecision.AcceptFirstUse:
                    return null;
                case CertificateDecision.RejectUntrusted:
                    return new UserMessage(
                        "Windows не доверяет сертификату сервера.",
                        "Так бывает с собственным сертификатом сервера Exchange. Сверьте отпечаток с администратором и, если "
                        + "он совпадает, нажмите «Доверять этому сертификату».");
                case CertificateDecision.RejectPinMismatch:
                    return new UserMessage(
                        "Сервер предъявил не тот сертификат, которому вы доверяли.",
                        "Сертификат могли заменить законно (например, продлили) — уточните у администратора. Пока это не "
                        + "подтверждено, не доверяйте новому сертификату: так выглядит и подмена сервера.");
                case CertificateDecision.RejectInterception:
                    return new UserMessage(
                        "Защищённое соединение перехватывает антивирус или прокси.",
                        "Добавьте адрес сервера в исключения проверки защищённых соединений антивируса и повторите.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(decision), decision, "Неизвестное решение по сертификату.");
            }
        }
    }
}
