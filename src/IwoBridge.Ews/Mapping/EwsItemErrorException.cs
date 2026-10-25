using System;
using Microsoft.Exchange.WebServices.Data;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>
    /// Ошибка сервера по отдельному элементу, которую EWS вернул в ответе, а не исключением (DeleteItems и другие пакетные
    /// операции). Код сервера сохраняется: по нему <see cref="EwsErrorMapper"/> решает, повторять ли запрос и
    /// останавливать ли пакет, так же, как для <see cref="ServiceResponseException"/> (у неё нет открытого конструктора).
    /// </summary>
    public sealed class EwsItemErrorException : Exception
    {
        public EwsItemErrorException(ServiceError errorCode, string? serverMessage)
            : base("Сервер не выполнил операцию с элементом: " + errorCode + (string.IsNullOrEmpty(serverMessage) ? "." : ". " + serverMessage))
        {
            ErrorCode = errorCode;
        }

        public ServiceError ErrorCode { get; }
    }
}
