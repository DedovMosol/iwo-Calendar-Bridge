using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Reading;
using IwoBridge.Core.Security;
using IwoBridge.Core.Time;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>Сеанс поверх ин-мемори календаря; манифест не пишет файлов.</summary>
    internal sealed class TestSession : ITargetSession
    {
        public TestSession(InMemoryCalendar calendar, ITargetPageReader? pageReader = null)
        {
            Calendar = calendar;
            Manifest = new MemoryManifest();
            Runner = new CleanupRunner(pageReader ?? calendar, calendar, calendar, calendar, Manifest);
        }

        public InMemoryCalendar Calendar { get; }

        public MemoryManifest Manifest { get; }

        public string TargetLabel => "user@example.test на mail.example.test";

        public string ConfirmationPhrase => "user@example.test";

        public TargetTimeZone TimeZone { get; } = TargetTimeZone.FromHours(3);

        public CleanupRunner Runner { get; }

        public bool IsDemo => false;

        public UserMessage Describe(Exception exception) => new UserMessage("Ошибка теста", "Подсказка", exception.Message);

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    internal sealed class MemoryManifest : ICleanupManifestWriter
    {
        public int Writes { get; private set; }

        public Task<string> WriteAsync(IReadOnlyList<CleanupManifestEntry> entries, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult("memory://manifest.csv");
        }
    }

    /// <summary>Чтение календаря всегда обрывается — как недоступный сервер.</summary>
    internal sealed class FailingPageReader : ITargetPageReader
    {
        public Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("сервер недоступен");
        }
    }

    /// <summary>Окна: заранее заданные ответы пользователя и запись того, что показывалось.</summary>
    internal sealed class FakeDialogs : IDialogService, IConnectionDialog
    {
        public bool ConfirmAnswer { get; set; } = true;

        public string? TypeOnConfirm { get; set; }

        public bool AskAnswer { get; set; }

        public DeletionConfirmationViewModel? LastConfirmation { get; private set; }

        public List<string> Questions { get; } = new List<string>();

        public string? ShownFile { get; private set; }

        public Func<ConnectionViewModel, bool>? OnConnection { get; set; }

        public int ConnectionShown { get; private set; }

        public bool ConfirmDeletion(DeletionConfirmationViewModel confirmation)
        {
            LastConfirmation = confirmation;
            if (TypeOnConfirm != null)
            {
                confirmation.TypedPhrase = TypeOnConfirm;
                return confirmation.CanConfirm;
            }

            return ConfirmAnswer;
        }

        public bool Ask(string title, string question)
        {
            Questions.Add(question);
            return AskAnswer;
        }

        public void ShowFileInFolder(string path)
        {
            ShownFile = path;
        }

        public bool Show(ConnectionViewModel connection)
        {
            ConnectionShown++;
            return OnConnection?.Invoke(connection) ?? false;
        }
    }

    internal sealed class MemorySettings : ISettingsStore
    {
        public ConnectionProfile? Profile { get; set; }

        public CredentialSecret? Credentials { get; set; }

        public int ProfileSaves { get; private set; }

        public Exception? LoadFailure { get; set; }

        public ConnectionProfile? LoadProfile()
        {
            if (LoadFailure != null)
            {
                throw LoadFailure;
            }

            return Profile;
        }

        public void SaveProfile(ConnectionProfile profile)
        {
            ProfileSaves++;
            Profile = profile;
        }

        public CredentialSecret? LoadCredentials() => Credentials;

        public void SaveCredentials(CredentialSecret credentials) => Credentials = credentials;
    }

    /// <summary>Подключение с заданным ответом проверки; запоминает, с каким профилем его вызывали.</summary>
    internal sealed class FakeConnector : ITargetConnector
    {
        public Func<ConnectionProfile, ConnectionCheckResult> Respond { get; set; } =
            _ => ConnectionCheckResult.Success(10, null, null, null);

        public List<ConnectionProfile> Checked { get; } = new List<ConnectionProfile>();

        public ConnectionProfile? OpenedProfile { get; private set; }

        public CredentialSecret? OpenedCredentials { get; private set; }

        public Task<ConnectionCheckResult> CheckAsync(ConnectionProfile profile, CredentialSecret credentials, CancellationToken cancellationToken)
        {
            Checked.Add(profile);
            return Task.FromResult(Respond(profile));
        }

        public ITargetSession Open(ConnectionProfile profile, CredentialSecret credentials)
        {
            OpenedProfile = profile;
            OpenedCredentials = credentials;
            return new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));
        }
    }
}
