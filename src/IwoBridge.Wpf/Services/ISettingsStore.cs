using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Где лежат настройки: профиль подключения — в файле, учётные данные — в диспетчере учётных данных Windows.
    /// Реализация — <see cref="AppSettingsStore"/>; в тестах — в памяти.
    /// </summary>
    public interface ISettingsStore
    {
        /// <summary>Профиль; <c>null</c> — настроек ещё нет.</summary>
        /// <exception cref="App.Configuration.ProfileLoadException">Файл есть, но повреждён или не читается.</exception>
        ConnectionProfile? LoadProfile();

        void SaveProfile(ConnectionProfile profile);

        /// <summary>Сохранённые учётные данные; <c>null</c> — их нет.</summary>
        CredentialSecret? LoadCredentials();

        void SaveCredentials(CredentialSecret credentials);
    }
}
