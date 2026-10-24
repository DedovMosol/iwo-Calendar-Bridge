using System;
using System.Linq;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>Галочка «Запомнить» в окне «Подключение» и добавленная почта в списке очистки. Данные синтетические.</summary>
    public sealed class MailboxAndRememberTests
    {
        private static ConnectionProfile Profile(string? mailbox = null) =>
            new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), mailbox, TimeSpan.Zero, null);

        private static Func<ConnectionViewModel, bool> Fill(string? mailbox, bool remember) => c =>
        {
            c.Server = "mail.example.test";
            c.Mailbox = mailbox ?? string.Empty;
            c.UserName = "DOMAIN\\user";
            c.SetPassword("secret");
            c.Offset = c.OffsetOptions[0];
            c.Remember = remember;
            c.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return c.OpenedSession != null;
        };

        [Fact]
        public void Remember_DefaultOn_ForNewConnection_SavesPassword()
        {
            var settings = new MemorySettings();
            var connection = new ConnectionViewModel(settings, new FakeConnector(), new FakeDialogs(), TimeSpan.Zero, null, null);

            Assert.True(connection.Remember);
            Assert.True(Fill(null, true)(connection));
            Assert.NotNull(settings.Credentials);
            Assert.Null(settings.UserName);
        }

        [Fact]
        public void RememberOff_DeletesSavedPassword_KeepsProfileAndUser_SessionStillOpens()
        {
            var settings = new MemorySettings { Profile = Profile(), Credentials = new CredentialSecret("DOMAIN\\user", "old") };
            var connection = new ConnectionViewModel(
                settings, new FakeConnector(), new FakeDialogs(), TimeSpan.Zero, settings.Profile, settings.Credentials);

            Assert.True(Fill(null, false)(connection));

            Assert.Null(settings.Credentials);
            Assert.NotNull(settings.Profile);
            Assert.Equal("DOMAIN\\user", settings.UserName);
            Assert.NotNull(connection.OpenedSession);
        }

        [Fact]
        public void Start_ProfileWithoutPassword_AsksPassword_UserPrefilled_RememberOff()
        {
            var dialogs = new FakeDialogs();
            ConnectionViewModel? shown = null;
            dialogs.OnConnection = c =>
            {
                shown = c;
                return false;
            };
            var settings = new MemorySettings { Profile = Profile(), UserName = "DOMAIN\\user" };
            var vm = new MainViewModel(settings, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero);

            Assert.False(vm.StartHidden());
            vm.Start();

            Assert.NotNull(shown);
            Assert.Equal("DOMAIN\\user", shown!.UserName);
            Assert.False(shown.Remember);
            Assert.False(vm.Cleanup.HasSession);
        }

        [Fact]
        public void AddMailbox_Remembered_AppearsSelected_AndAfterRestart()
        {
            var mailboxes = new MemoryMailboxes();
            var dialogs = new FakeDialogs();
            var target = new MemorySettings { Profile = Profile(), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);
            vm.Start();
            Assert.False(vm.CanRemoveMailbox);

            dialogs.OnConnection = Fill("other@example.test", true);
            vm.AddMailboxCommand.Execute(null);

            Assert.Equal(2, vm.CleanupChoices.Count);
            Assert.StartsWith("Почта: other@example.test", vm.CleanupChoice!.Label, StringComparison.Ordinal);
            Assert.True(vm.CanRemoveMailbox);
            Assert.Single(mailboxes.Saved());

            var restarted = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);
            restarted.Start();
            Assert.Contains(restarted.CleanupChoices, c => c.Label.StartsWith("Почта: other@example.test", StringComparison.Ordinal));
        }

        [Fact]
        public void AddMailbox_NotRemembered_OnlyThisSession()
        {
            var mailboxes = new MemoryMailboxes();
            var dialogs = new FakeDialogs();
            var target = new MemorySettings { Profile = Profile(), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);
            vm.Start();

            dialogs.OnConnection = Fill("other@example.test", false);
            vm.AddMailboxCommand.Execute(null);

            Assert.Equal(2, vm.CleanupChoices.Count);
            Assert.Empty(mailboxes.Saved());
        }

        [Fact]
        public void AddMailbox_SameAsTarget_SelectsExisting_NoDuplicate()
        {
            var mailboxes = new MemoryMailboxes();
            var dialogs = new FakeDialogs();
            var target = new MemorySettings { Profile = Profile("box@example.test"), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);
            vm.Start();

            dialogs.OnConnection = Fill("box@example.test", true);
            vm.AddMailboxCommand.Execute(null);

            Assert.Single(vm.CleanupChoices);
            Assert.False(vm.CanRemoveMailbox);
            Assert.Empty(mailboxes.Saved());
        }

        [Fact]
        public void RemoveMailbox_Confirmed_DeletesSaved_DisposesSession_SelectsTarget()
        {
            var mailboxes = new MemoryMailboxes();
            var dialogs = new FakeDialogs();
            var target = new MemorySettings { Profile = Profile(), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);
            vm.Start();
            dialogs.OnConnection = Fill("other@example.test", true);
            vm.AddMailboxCommand.Execute(null);
            var added = (TestSession)vm.CleanupChoices.Last().Session;

            dialogs.AskAnswer = true;
            vm.RemoveMailboxCommand.Execute(null);

            Assert.Single(vm.CleanupChoices);
            Assert.Empty(mailboxes.Saved());
            Assert.True(added.IsDisposed);
            Assert.False(vm.CanRemoveMailbox);
        }

        [Fact]
        public void Start_SavedMailboxWithoutPassword_Dropped()
        {
            var mailboxes = new MemoryMailboxes();
            var orphan = (MemorySettings)mailboxes.CreateNew();
            orphan.SaveProfile(Profile("other@example.test"), null);
            var dialogs = new FakeDialogs();
            var target = new MemorySettings { Profile = Profile(), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, mailboxes: mailboxes);

            vm.Start();

            Assert.Single(vm.CleanupChoices);
            Assert.True(orphan.Deleted);
        }
    }
}
