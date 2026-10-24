using System;
using System.IO;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Настройки программы: профиль — в файле <c>config\connection.json</c>, учётные данные — в диспетчере учётных
    /// данных Windows под именем <see cref="CredentialTargets.Ews"/>.
    /// </summary>
    public sealed class AppSettingsStore : ISettingsStore
    {
        private readonly string _profilePath;
        private readonly ICredentialStore _credentials;

        public AppSettingsStore(string profilePath, ICredentialStore credentials)
        {
            _profilePath = profilePath ?? throw new ArgumentNullException(nameof(profilePath));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        }

        public ConnectionProfile? LoadProfile()
        {
            return File.Exists(_profilePath) ? ConnectionProfileFile.Load(_profilePath) : null;
        }

        public void SaveProfile(ConnectionProfile profile)
        {
            ConnectionProfileFile.Save(_profilePath, profile);
        }

        public CredentialSecret? LoadCredentials()
        {
            return _credentials.TryRead(CredentialTargets.Ews);
        }

        public void SaveCredentials(CredentialSecret credentials)
        {
            _credentials.Save(CredentialTargets.Ews, credentials);
        }
    }
}
