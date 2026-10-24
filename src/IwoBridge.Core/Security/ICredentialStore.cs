namespace IwoBridge.Core.Security
{
    /// <summary>
    /// Хранилище учётных данных приёмника. Реализуется поверх диспетчера учётных данных Windows (DPAPI);
    /// ядро зависит только от этого порта (DIP). Пароль в коде/конфигурации/логах не хранится.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>Читает учётные данные по имени цели; <c>null</c>, если их нет.</summary>
        CredentialSecret? TryRead(string target);

        /// <summary>Сохраняет (или заменяет) учётные данные под именем цели.</summary>
        void Save(string target, CredentialSecret secret);

        /// <summary>Удаляет учётные данные под именем цели. Записи нет — ничего не делает.</summary>
        void Delete(string target);
    }

    /// <summary>
    /// Имена целей в диспетчере учётных данных — константы продукта (не данные организации). Те же имена
    /// используются в командах <c>cmdkey</c> из README.
    /// </summary>
    public static class CredentialTargets
    {
        /// <summary>Учётная запись для входа на сервер-приёмник по EWS.</summary>
        public const string Ews = "IwoCalendarBridge/ews";

        /// <summary>Учётные данные подключения «Источник по EWS» (копирование с другого сервера).</summary>
        public const string EwsSource = "IwoCalendarBridge/ews-source";

        /// <summary>Начало имени записи добавленной почты (для очистки); дальше — номер почты в списке программы.</summary>
        public const string MailboxPrefix = "IwoCalendarBridge/mailbox/";
    }
}
