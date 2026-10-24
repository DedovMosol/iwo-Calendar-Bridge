using System;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Time;
using IwoBridge.Ews;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Сеанс очистки настоящего календаря по EWS: адаптер EWS + сценарий очистки + манифест в папке программы.
    /// Сеть при создании не трогается — соединение устанавливается при первом запросе.
    /// </summary>
    public sealed class EwsSession : ITargetSession
    {
        private readonly EwsTarget _target;

        public EwsSession(ConnectionProfile profile, CredentialSecret credentials, string manifestDirectory)
        {
            if (profile is null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (credentials is null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            _target = new EwsTarget(profile, credentials);
            EwsCalendarGateway calendar = _target.Calendar;
            TimeZone = profile.TimeZone;
            Runner = new CleanupRunner(
                calendar, calendar, calendar, calendar, new CsvCleanupManifestWriter(manifestDirectory, profile.TimeZone));

            // Резервный адрес виден в шапке: человек знает, что программа может уйти на него, если основной недоступен.
            string server = ServerAddress.ToDisplay(profile.EwsUrl)
                + (profile.AlternateEwsUrl is null ? string.Empty : " (резерв: " + ServerAddress.ToDisplay(profile.AlternateEwsUrl) + ")");
            if (profile.MailboxAddress is null)
            {
                TargetLabel = "Свой ящик " + credentials.UserName + " на " + server;
                ConfirmationPhrase = credentials.UserName;
            }
            else
            {
                TargetLabel = profile.MailboxAddress + " на " + server;
                ConfirmationPhrase = profile.MailboxAddress;
            }
        }

        public string TargetLabel { get; }

        public string ConfirmationPhrase { get; }

        public TargetTimeZone TimeZone { get; }

        public CleanupRunner Runner { get; }

        public bool IsDemo => false;

        public UserMessage Describe(Exception exception)
        {
            return exception is null
                ? ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown)
                : ErrorMessages.For(EwsTarget.Classify(exception), EwsTarget.Explain(exception));
        }

        public void Dispose()
        {
            _target.Dispose();
        }
    }
}
