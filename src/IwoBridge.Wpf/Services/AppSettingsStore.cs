using System;
using System.IO;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Настройки одного подключения: профиль — в файле (<c>config\connection.json</c> для приёмника,
    /// <c>config\source-connection.json</c> для источника по EWS, <c>config\mailboxes\*.json</c> для добавленной почты),
    /// пароль — в диспетчере учётных данных Windows под своим именем. Нет файла — подключение не настроено (<c>null</c>).
    /// </summary>
    public sealed class AppSettingsStore : ISettingsStore
    {
        private readonly string _profilePath;
        private readonly ICredentialStore _credentials;
        private readonly string _credentialTarget;

        public AppSettingsStore(string profilePath, ICredentialStore credentials, string credentialTarget = CredentialTargets.Ews)
        {
            _profilePath = profilePath ?? throw new ArgumentNullException(nameof(profilePath));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _credentialTarget = string.IsNullOrEmpty(credentialTarget) ? throw new ArgumentException("Нужно имя записи.", nameof(credentialTarget)) : credentialTarget;
        }

        public ConnectionProfile? LoadProfile()
        {
            return File.Exists(_profilePath) ? ConnectionProfileFile.Load(_profilePath) : null;
        }

        public void SaveProfile(ConnectionProfile profile, string? userName)
        {
            ConnectionProfileFile.Save(_profilePath, profile, userName);
        }

        public string? LoadUserName()
        {
            return ConnectionProfileFile.ReadUserName(_profilePath);
        }

        public CredentialSecret? LoadCredentials()
        {
            return _credentials.TryRead(_credentialTarget);
        }

        public void SaveCredentials(CredentialSecret credentials)
        {
            _credentials.Save(_credentialTarget, credentials);
        }

        public void DeleteCredentials()
        {
            _credentials.Delete(_credentialTarget);
        }

        public void Delete()
        {
            // Сначала пароль: если диспетчер учётных данных недоступен, файл остаётся, и подключение видно в списке —
            // его можно убрать ещё раз, а не оставить пароль без файла.
            _credentials.Delete(_credentialTarget);
            if (File.Exists(_profilePath))
            {
                File.Delete(_profilePath);
            }
        }
    }
}
