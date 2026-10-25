using System;
using System.Linq;
using System.Windows.Controls;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Окно «Учётные записи»: все подключения одним списком с состоянием пароля; изменить, забыть пароль, удалить —
    /// для приёмника, источника по EWS и добавленной почты; после удаления копирование и очистка не держат старое
    /// подключение; отказ в вопросе ничего не меняет; в демонстрационном режиме — только просмотр. Данные синтетические.
    /// </summary>
    public sealed class AccountsTests
    {
        private static ConnectionProfile Profile(string? mailbox = null) =>
            new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), mailbox, TimeSpan.Zero, null);

        private static MemorySettings Saved(string? mailbox = null) =>
            new MemorySettings { Profile = Profile(mailbox), Credentials = new CredentialSecret("DOMAIN\\user", "secret") };

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

        private static AccountsViewModel Open(MainViewModel vm, FakeDialogs dialogs)
        {
            vm.AccountsCommand.Execute(null);
            return dialogs.AccountsShown!;
        }

        [Fact]
        public void List_TargetSourceAndMailbox_WithPasswordState_CommandsFollowSelection()
        {
            var dialogs = new FakeDialogs();
            var vm = new MainViewModel(Saved(), new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, new MemorySettings(), mailboxes: new MemoryMailboxes());
            vm.Start();
            AccountsViewModel accounts = Open(vm, dialogs);
            dialogs.OnConnection = Fill("other@example.test", remember: false);
            accounts.AddCommand.Execute(null);

            Assert.Equal(new[] { AccountRole.Target, AccountRole.Source, AccountRole.Mailbox }, accounts.Rows.Select(r => r.Role));
            AccountRow target = accounts.Rows[0], source = accounts.Rows[1], mailbox = accounts.Rows[2];
            Assert.Equal("ящик учётной записи", target.Mailbox);
            Assert.Equal("mail.example.test", target.Server);
            Assert.Equal("DOMAIN\\user", target.UserName);
            Assert.Equal("сохранён в Windows", target.Password);
            Assert.False(source.IsConfigured);
            Assert.Equal("other@example.test", mailbox.Mailbox);
            Assert.Equal("до закрытия программы", mailbox.Password);
            Assert.Same(mailbox, accounts.Selected); // добавленная — выбрана
            Assert.Contains("добавлена", accounts.Status);

            Assert.False(accounts.ForgetPasswordCommand.CanExecute(null)); // пароль не сохранён — забывать нечего
            Assert.True(accounts.DeleteCommand.CanExecute(null));

            accounts.Selected = source;
            Assert.Equal("_Настроить…", accounts.EditText);
            Assert.False(accounts.DeleteCommand.CanExecute(null));
            Assert.True(accounts.EditCommand.CanExecute(null));

            accounts.Selected = target;
            Assert.Equal("_Изменить…", accounts.EditText);
            Assert.True(accounts.ForgetPasswordCommand.CanExecute(null));
            Assert.Null(accounts.ReadOnlyReason);
        }

        [Fact]
        public void ForgetPassword_DeletedFromWindows_UserKeptInProfile_SessionWorksUntilExit_NextStartAsks()
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            MemorySettings target = Saved();
            var log = new IwoBridge.App.History.MemoryOperationLog();
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, log: log);
            vm.Start();
            AccountsViewModel accounts = Open(vm, dialogs);

            accounts.ForgetPasswordCommand.Execute(null);

            Assert.Null(target.Credentials);
            Assert.NotNull(target.Profile);
            Assert.Equal("DOMAIN\\user", target.UserName);
            Assert.True(vm.Copy.HasTarget);
            Assert.Equal("до закрытия программы", accounts.Rows[0].Password);
            Assert.False(accounts.StatusIsError);
            Assert.Contains(log.Messages, m => m.StartsWith("Учётные записи: пароль удалён", StringComparison.Ordinal));

            ConnectionViewModel? asked = null;
            dialogs.OnConnection = c =>
            {
                asked = c;
                return false;
            };
            var restarted = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero);
            Assert.False(restarted.StartHidden());
            restarted.Start();
            Assert.Equal("DOMAIN\\user", asked!.UserName);
        }

        [Fact]
        public void DeleteTarget_Confirmed_SettingsAndPasswordGone_CopyAndCleanupDropIt()
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            MemorySettings target = Saved();
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero);
            vm.Start();
            var session = (TestSession)vm.CleanupChoice!.Session!;
            AccountsViewModel accounts = Open(vm, dialogs);

            accounts.DeleteCommand.Execute(null);

            Assert.True(target.Deleted);
            Assert.False(vm.Copy.HasTarget);
            Assert.Equal("Приёмник не настроен", vm.TargetLabel);
            Assert.False(vm.HasCleanupChoices);
            Assert.True(session.IsDisposed);
            Assert.False(accounts.Rows[0].IsConfigured);
            Assert.Equal("_Настроить…", accounts.EditText);
            Assert.Contains("удалено", accounts.Status);
            Assert.Contains("События в календаре ящика не меняются", string.Join(" ", dialogs.Questions), StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_Refused_NothingChanges()
        {
            var dialogs = new FakeDialogs { AskAnswer = false };
            MemorySettings target = Saved();
            var vm = new MainViewModel(target, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero);
            vm.Start();
            AccountsViewModel accounts = Open(vm, dialogs);

            accounts.DeleteCommand.Execute(null);
            accounts.ForgetPasswordCommand.Execute(null);

            Assert.False(target.Deleted);
            Assert.NotNull(target.Credentials);
            Assert.True(vm.Copy.HasTarget);
            Assert.Null(accounts.Status);
        }

        [Fact]
        public void DeleteSource_CopyLosesEwsSource_TargetStays()
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            MemorySettings source = Saved("src@example.test");
            var vm = new MainViewModel(Saved(), new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, source);
            vm.Start();
            Assert.True(vm.Copy.HasEwsSource);
            AccountsViewModel accounts = Open(vm, dialogs);
            accounts.Selected = accounts.Rows.Single(r => r.Role == AccountRole.Source);

            accounts.DeleteCommand.Execute(null);

            Assert.True(source.Deleted);
            Assert.False(vm.Copy.HasEwsSource);
            Assert.True(vm.Copy.HasTarget);
            Assert.Single(vm.CleanupChoices);
            Assert.Equal(AccountRole.Source, accounts.Selected!.Role); // выбрана та же строка — теперь «не настроен»
        }

        [Fact]
        public void Edit_NotConfiguredSource_OpensConnection_ThenConfigured()
        {
            var dialogs = new FakeDialogs();
            var source = new MemorySettings();
            var vm = new MainViewModel(Saved(), new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, source);
            vm.Start();
            AccountsViewModel accounts = Open(vm, dialogs);
            accounts.Selected = accounts.Rows[1];
            dialogs.OnConnection = Fill("src@example.test", remember: true);

            accounts.EditCommand.Execute(null);

            Assert.True(vm.Copy.HasEwsSource);
            Assert.True(accounts.Rows[1].IsConfigured);
            Assert.Equal("src@example.test", accounts.Rows[1].Mailbox);
            Assert.Equal("Подключение сохранено.", accounts.Status);
        }

        [Fact]
        public void Demo_ListShown_ReadOnly()
        {
            var dialogs = new FakeDialogs();
            var vm = new MainViewModel(new DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs);
            AccountsViewModel accounts = Open(vm, dialogs);

            Assert.Equal(2, accounts.Rows.Count);
            Assert.NotNull(accounts.ReadOnlyReason);
            Assert.False(accounts.AddCommand.CanExecute(null));
            Assert.False(accounts.EditCommand.CanExecute(null));
            Assert.False(accounts.ForgetPasswordCommand.CanExecute(null));
            Assert.False(accounts.DeleteCommand.CanExecute(null));
            Assert.False(vm.CanEditConnections);
        }

        [Fact]
        public void AccountsWindow_NoBindingErrors_UniqueAccessKeys_ButtonsInside()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs();
                    var vm = new MainViewModel(Saved(), new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero, new MemorySettings(), mailboxes: new MemoryMailboxes());
                    vm.Start();
                    var window = new AccountsWindow(Open(vm, dialogs));
                    try
                    {
                        Visuals.ShowOffscreen(window, 640, 340);
                        await Visuals.Settle(window);

                        var root = (System.Windows.FrameworkElement)window.Content;
                        foreach (Button button in Visuals.All<Button>(window).Where(b => b.IsVisible && b.Content is string))
                        {
                            System.Windows.Rect bounds = Visuals.BoundsIn(button, root);
                            Assert.True(bounds.Right <= root.ActualWidth + 0.5 && bounds.Bottom <= root.ActualHeight + 0.5, $"«{button.Content}» за краем окна: {bounds}");
                        }

                        Assert.Equal(2, Visuals.ByAutomationId<DataGrid>(window, "AccountRows").Items.Count);
                        string[] keys = Visuals.All<System.Windows.Controls.AccessText>(window).Where(t => t.IsVisible && t.AccessKey != default(char))
                            .Select(t => char.ToLowerInvariant(t.AccessKey).ToString()).ToArray();
                        Assert.Equal(keys.Length, keys.Distinct().Count());
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }
    }
}
