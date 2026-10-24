using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Errors;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>
    /// Перевод ошибок EWS в нейтральный <see cref="OperationErrorKind"/> для единой политики повторов
    /// (<see cref="ErrorClassifier"/>, <see cref="RetryPolicy"/>). Коды <see cref="ServiceError"/> отображаются чистой
    /// функцией (тестируется); исключения транспорта разбираются по типу и статусу. Справочник кодов — «ResponseCode»
    /// в документации EWS (learn.microsoft.com/exchange/client-developer/web-service-reference/responsecode).
    /// </summary>
    public static class EwsErrorMapper
    {
        public static OperationErrorKind Map(ServiceError error)
        {
            switch (error)
            {
                case ServiceError.ErrorAccessDenied:
                // Календарь чужого ящика не найден — на деле нет прав на него (или ящик без календаря).
                case ServiceError.ErrorFolderNotFound:
                    return OperationErrorKind.AccessDenied;
                case ServiceError.ErrorNonExistentMailbox:
                    return OperationErrorKind.MailboxNotFound;

                // Временные: сервер занят, ящик переносится или хранилище недоступно, таймаут на стороне сервера.
                case ServiceError.ErrorServerBusy:
                case ServiceError.ErrorMailboxMoveInProgress:
                case ServiceError.ErrorMailboxStoreUnavailable:
                case ServiceError.ErrorInternalServerTransientError:
                    return OperationErrorKind.ServerBusy;
                case ServiceError.ErrorTimeoutExpired:
                    return OperationErrorKind.Timeout;
                case ServiceError.ErrorConnectionFailed:
                    return OperationErrorKind.ConnectionReset;

                case ServiceError.ErrorQuotaExceeded:
                    return OperationErrorKind.QuotaExceeded;
                case ServiceError.ErrorIrresolvableConflict:
                    return OperationErrorKind.VersionConflict;
                case ServiceError.ErrorSchemaValidation:
                    return OperationErrorKind.SchemaError;

                // Лимиты выборки: запрос надо уменьшить, а не повторять тот же.
                case ServiceError.ErrorExceededFindCountLimit:
                case ServiceError.ErrorCalendarViewRangeTooBig:
                    return OperationErrorKind.RequestTooLarge;
                default:
                    return OperationErrorKind.Unknown;
            }
        }

        public static OperationErrorKind MapException(Exception exception)
        {
            switch (exception)
            {
                case ServiceResponseException sre:
                    return Map(sre.ErrorCode);
                case ServiceRequestException _ when exception.InnerException != null:
                    return MapException(exception.InnerException);
                case WebException web:
                    return MapWebException(web);
                case TimeoutException _:
                    return OperationErrorKind.Timeout;
                case SocketException _:
                case IOException _:
                    return OperationErrorKind.ConnectionReset;
                default:
                    return OperationErrorKind.Unknown;
            }
        }

        /// <summary>
        /// Пауза, которую просит сервер (Exchange 2010+ присылает BackOffMilliseconds при ErrorServerBusy); иначе <c>null</c>.
        /// </summary>
        public static TimeSpan? ServerBackOff(Exception exception)
        {
            ServiceResponseException? sre = exception as ServiceResponseException
                ?? exception?.InnerException as ServiceResponseException;
            if (sre?.Response?.ErrorDetails != null
                && sre.Response.ErrorDetails.TryGetValue("BackOffMilliseconds", out string value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int milliseconds)
                && milliseconds > 0)
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }

            return null;
        }

        private static OperationErrorKind MapWebException(WebException web)
        {
            switch (web.Status)
            {
                case WebExceptionStatus.Timeout:
                    return OperationErrorKind.Timeout;
                // Имя сервера не разрешилось — для пользователя это та же «нет связи с сервером».
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                    return OperationErrorKind.ConnectionReset;
                case WebExceptionStatus.TrustFailure:
                    // Сертификат отклонён проверкой (закрепление, цепочка или перехват HTTPS).
                    return OperationErrorKind.CertificatePinMismatch;
                case WebExceptionStatus.SecureChannelFailure:
                    // TLS не согласован: протокол или шифры (например, TLS 1.0 отключён в ОС).
                    return OperationErrorKind.TlsNegotiationFailed;
                case WebExceptionStatus.ProtocolError:
                    return MapHttpStatus(web);
                default:
                    return OperationErrorKind.Unknown;
            }
        }

        private static OperationErrorKind MapHttpStatus(WebException web)
        {
            int code;
            try
            {
                if (!(web.Response is HttpWebResponse response))
                {
                    return OperationErrorKind.Unknown;
                }

                code = (int)response.StatusCode;
            }
            catch (ObjectDisposedException)
            {
                // Ответ уже закрыт библиотекой — код не прочитать.
                return OperationErrorKind.Unknown;
            }

            switch (code)
            {
                case 401:
                    return OperationErrorKind.AuthenticationFailed;
                case 403:
                    return OperationErrorKind.AccessDenied;
                case 404:
                    return OperationErrorKind.EndpointNotFound;
                case 413:
                    return OperationErrorKind.RequestTooLarge;
                case 503:
                    return OperationErrorKind.ServerBusy;
                default:
                    return OperationErrorKind.Unknown;
            }
        }
    }
}
