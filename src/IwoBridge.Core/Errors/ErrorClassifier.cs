using System;

namespace IwoBridge.Core.Errors
{
    /// <summary>
    /// Единственное место классификации ошибок (DRY). Чистая функция от <see cref="OperationErrorKind"/>
    /// к <see cref="ErrorCategory"/>; повторы по категории выполняет <see cref="RetryPolicy"/>.
    ///
    /// Ключевое правило безопасности: <see cref="OperationErrorKind.AuthenticationFailed"/> никогда не
    /// повторяется — доменная политика заблокирует учётную запись. Нераспознанное
    /// (<see cref="OperationErrorKind.Unknown"/>) считается постоянным: лучше показать, чем вслепую
    /// повторять.
    /// </summary>
    public static class ErrorClassifier
    {
        public static ErrorCategory Classify(OperationErrorKind kind)
        {
            switch (kind)
            {
                case OperationErrorKind.Timeout:
                case OperationErrorKind.ConnectionReset:
                case OperationErrorKind.ServerBusy:
                case OperationErrorKind.ComBusy:
                    return ErrorCategory.Transient;

                case OperationErrorKind.VersionConflict:
                    return ErrorCategory.VersionConflict;

                case OperationErrorKind.AuthenticationFailed:
                    return ErrorCategory.Authentication;

                case OperationErrorKind.AccessDenied:
                case OperationErrorKind.MailboxNotFound:
                case OperationErrorKind.EndpointNotFound:
                case OperationErrorKind.QuotaExceeded:
                case OperationErrorKind.RequestTooLarge:
                case OperationErrorKind.SchemaError:
                case OperationErrorKind.CertificatePinMismatch:
                case OperationErrorKind.TlsNegotiationFailed:
                case OperationErrorKind.Unknown:
                    return ErrorCategory.Permanent;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид ошибки.");
            }
        }

        /// <summary>
        /// «До сервера по этому адресу не достучаться»: обрыв или отказ соединения, имя не разрешилось, нет ответа.
        /// Только при такой ошибке подключение переходит на резервный адрес. Ошибки входа, прав, сертификата и TLS
        /// сюда не относятся: по другому адресу отвечает тот же сервер, и они повторились бы — а ошибка входа ещё и
        /// приблизила бы блокировку учётной записи. «Сервер занят» — тоже нет: занят сам сервер, а не адрес.
        /// </summary>
        public static bool IsUnreachable(OperationErrorKind kind)
        {
            return kind == OperationErrorKind.ConnectionReset || kind == OperationErrorKind.Timeout;
        }

        /// <summary>
        /// Ошибка не одного события, а всего подключения: следующие события упали бы так же. Пакетная работа (копирование)
        /// на ней останавливается сразу, а не перебирает остальные — повтор неверного входа ещё и приблизил бы блокировку
        /// учётной записи. Временные ошибки сюда попадают уже после всех повторов.
        /// </summary>
        /// <summary>
        /// Ошибка, которую само время не исправит: неверный вход, нет прав, нет ящика или адреса, чужой сертификат, TLS.
        /// Автоматические повторы по расписанию на ней приостанавливаются до правки подключения — повтор неверного входа
        /// каждые N минут заблокировал бы учётную запись. Сеть и «сервер занят» сюда не относятся: пройдут сами.
        /// </summary>
        public static bool NeedsAttention(OperationErrorKind kind)
        {
            switch (kind)
            {
                case OperationErrorKind.AuthenticationFailed:
                case OperationErrorKind.AccessDenied:
                case OperationErrorKind.MailboxNotFound:
                case OperationErrorKind.EndpointNotFound:
                case OperationErrorKind.CertificatePinMismatch:
                case OperationErrorKind.TlsNegotiationFailed:
                    return true;
                default:
                    return false;
            }
        }

        public static bool StopsBatch(OperationErrorKind kind)
        {
            switch (kind)
            {
                case OperationErrorKind.AuthenticationFailed:
                case OperationErrorKind.AccessDenied:
                case OperationErrorKind.MailboxNotFound:
                case OperationErrorKind.EndpointNotFound:
                case OperationErrorKind.CertificatePinMismatch:
                case OperationErrorKind.TlsNegotiationFailed:
                case OperationErrorKind.QuotaExceeded:
                case OperationErrorKind.ConnectionReset:
                case OperationErrorKind.Timeout:
                case OperationErrorKind.ServerBusy:
                    return true;
                default:
                    return false;
            }
        }
    }
}
