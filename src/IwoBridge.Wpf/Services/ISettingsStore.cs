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

        /// <summary>
        /// Сохраняет профиль. <paramref name="userName"/> — учётная запись, которую нужно помнить без пароля (галочка
        /// «Запомнить» снята); <c>null</c> — её помнит запись в диспетчере учётных данных.
        /// </summary>
        void SaveProfile(ConnectionProfile profile, string? userName);

        /// <summary>Учётная запись, сохранённая в профиле без пароля; <c>null</c> — её там нет.</summary>
        string? LoadUserName();

        /// <summary>Сохранённые учётные данные; <c>null</c> — их нет.</summary>
        CredentialSecret? LoadCredentials();

        void SaveCredentials(CredentialSecret credentials);

        /// <summary>Удаляет сохранённые учётные данные (пароль больше не помнится). Их нет — ничего не делает.</summary>
        void DeleteCredentials();

        /// <summary>Удаляет подключение целиком: профиль и учётные данные.</summary>
        void Delete();
    }
}
