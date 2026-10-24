namespace IwoBridge.Core.Transport
{
    /// <summary>Решение по сертификату сервера при подключении.</summary>
    public enum CertificateDecision
    {
        /// <summary>Отпечаток совпал с закреплённым — принять.</summary>
        AcceptPinned,

        /// <summary>Закрепления ещё нет, цепочка доверенная — принять и запомнить отпечаток (TOFU).</summary>
        AcceptFirstUse,

        /// <summary>Отпечаток не совпал с закреплённым — отклонить.</summary>
        RejectPinMismatch,

        /// <summary>Похоже на перехват HTTPS (антивирус/прокси пересоздал сертификат) — отклонить.</summary>
        RejectInterception,

        /// <summary>Первое подключение, цепочка недоверенная и не перехват — отклонить.</summary>
        RejectUntrusted,
    }
}
