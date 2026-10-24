namespace IwoBridge.Core.Errors
{
    /// <summary>
    /// Нормализованный вид ошибки операции. Адаптеры (EWS/Outlook) переводят свои исключения, коды ответа и
    /// HTTP-статусы в это перечисление, а классификацию делает <see cref="ErrorClassifier"/> (SoC): ядро не
    /// зависит от типов исключений внешних библиотек.
    /// </summary>
    public enum OperationErrorKind
    {
        /// <summary>Таймаут запроса.</summary>
        Timeout,

        /// <summary>Разрыв/сброс соединения.</summary>
        ConnectionReset,

        /// <summary>Сервер занят / троттлинг (например, ErrorServerBusy).</summary>
        ServerBusy,

        /// <summary>COM занят (Outlook): RPC_E_SERVERCALL_RETRYLATER / RPC_E_CALL_REJECTED.</summary>
        ComBusy,

        /// <summary>401 после корректного NTLM-обмена — неверные учётные данные.</summary>
        AuthenticationFailed,

        /// <summary>Устаревший ChangeKey — конфликт версий элемента.</summary>
        VersionConflict,

        /// <summary>Нет прав (ErrorAccessDenied).</summary>
        AccessDenied,

        /// <summary>Ящика с указанным адресом нет на сервере (ErrorNonExistentMailbox).</summary>
        MailboxNotFound,

        /// <summary>По адресу нет службы EWS (HTTP 404): неверный путь или виртуальный каталог.</summary>
        EndpointNotFound,

        /// <summary>Превышена квота ящика (ErrorQuotaExceeded).</summary>
        QuotaExceeded,

        /// <summary>Запрос слишком большой (HTTP 413).</summary>
        RequestTooLarge,

        /// <summary>Ошибка схемы запроса.</summary>
        SchemaError,

        /// <summary>Сертификат сервера отклонён проверкой: закрепление, недоверенная цепочка или перехват HTTPS.</summary>
        CertificatePinMismatch,

        /// <summary>
        /// TLS не согласован (протокол или шифры): например, TLS 1.0 отключён в ОС, а сервер умеет только его.
        /// </summary>
        TlsNegotiationFailed,

        /// <summary>Прочее, не распознанное адаптером.</summary>
        Unknown,
    }
}
