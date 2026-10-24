using System;
using System.Linq;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Transport;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Мелкие части окна: подтверждение удаления, тексты ошибок для каждого вида, склонения, список поясов, главное
    /// окно при запуске, демонстрационный режим.
    /// </summary>
    public sealed class SmallPartsTests
    {
        [Theory]
        [InlineData("user@example.test", true)]
        [InlineData("  USER@EXAMPLE.TEST  ", true)]
        [InlineData("user@example", false)]
        [InlineData("", false)]
        public void Confirmation_PhraseMatchIgnoresCaseAndSpaces(string typed, bool expected)
        {
            var vm = new DeletionConfirmationViewModel("ящик", "user@example.test", "1 событие", hardDelete: false) { TypedPhrase = typed };

            Assert.Equal(expected, vm.CanConfirm);
        }

        [Fact]
        public void Confirmation_HardDelete_NeedsAcknowledgement()
        {
            var vm = new DeletionConfirmationViewModel("ящик", "user@example.test", "1 событие", hardDelete: true)
            {
                TypedPhrase = "user@example.test",
            };

            Assert.False(vm.CanConfirm);
            vm.UnderstoodIrreversible = true;
            Assert.True(vm.CanConfirm);
        }

        [Fact]
        public void EveryErrorKind_HasTitleAndHint()
        {
            foreach (OperationErrorKind kind in Enum.GetValues(typeof(OperationErrorKind)).Cast<OperationErrorKind>())
            {
                UserMessage message = ErrorMessages.For(kind, "детали");
                Assert.False(string.IsNullOrWhiteSpace(message.Title), kind.ToString());
                Assert.False(string.IsNullOrWhiteSpace(message.Hint), kind.ToString());
                Assert.Equal("детали", message.Details);
            }
        }

        [Fact]
        public void EveryCertificateRejection_HasMessage_AcceptancesDoNot()
        {
            foreach (CertificateDecision decision in Enum.GetValues(typeof(CertificateDecision)).Cast<CertificateDecision>())
            {
                UserMessage? message = ErrorMessages.ForCertificate(decision);
                Assert.Equal(!CertificatePinningPolicy.IsAccepted(decision), message != null);
            }
        }

        [Fact]
        public void AuthenticationMessage_SaysNoRetry()
        {
            Assert.Contains("не повторяла", ErrorMessages.For(OperationErrorKind.AuthenticationFailed).Hint);
        }

        [Theory]
        [InlineData(1, "событие")]
        [InlineData(2, "события")]
        [InlineData(4, "события")]
        [InlineData(5, "событий")]
        [InlineData(11, "событий")]
        [InlineData(12, "событий")]
        [InlineData(14, "событий")]
        [InlineData(21, "событие")]
        [InlineData(22, "события")]
        [InlineData(25, "событий")]
        [InlineData(111, "событий")]
        [InlineData(0, "событий")]
        public void Plural_RussianRules(int count, string expected)
        {
            Assert.Equal(expected, RussianText.Plural(count, "событие", "события", "событий"));
        }

        [Fact]
        public void OffsetOptions_SortedUnique_IncludeHalfHours_AndNonStandardSaved()
        {
            var options = OffsetOption.All(TimeSpan.FromHours(3), TimeSpan.FromMinutes(17));

            Assert.Equal(options.Select(o => o.Offset).OrderBy(o => o), options.Select(o => o.Offset));
            Assert.Equal(options.Count, options.Select(o => o.Offset).Distinct().Count());
            Assert.Contains(options, o => o.Label == "UTC+05:30");
            Assert.Contains(options, o => o.Offset == TimeSpan.FromMinutes(17));
            Assert.Single(options, o => o.Label.Contains("сейчас"));
        }

        [Fact]
        public void Main_NoSettings_OpensConnectionWindowAtStart()
        {
            var dialogs = new FakeDialogs();
            var vm = new MainViewModel(new MemorySettings(), new FakeConnector(), dialogs, dialogs, () => TimeSpan.FromHours(3));

            vm.Start();

            Assert.Equal(1, dialogs.ConnectionShown);
            Assert.False(vm.Cleanup.HasSession);
        }

        [Fact]
        public void Main_SavedSettings_OpensSessionWithoutDialog()
        {
            var dialogs = new FakeDialogs();
            var settings = new MemorySettings
            {
                Profile = new Core.Configuration.ConnectionProfile(
                    new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null),
                Credentials = new Core.Security.CredentialSecret("DOMAIN\\user", "secret"),
            };
            var connector = new FakeConnector();
            var vm = new MainViewModel(settings, connector, dialogs, dialogs, () => TimeSpan.Zero);

            vm.Start();

            Assert.Equal(0, dialogs.ConnectionShown);
            Assert.True(vm.Cleanup.HasSession);
            Assert.NotNull(connector.OpenedProfile);
        }

        [Fact]
        public void Main_CorruptSettings_ShowsConnectionWithError()
        {
            var dialogs = new FakeDialogs();
            ConnectionViewModel? shown = null;
            dialogs.OnConnection = c =>
            {
                shown = c;
                return false;
            };
            var settings = new MemorySettings { LoadFailure = new ProfileLoadException("Файл повреждён.") };
            var vm = new MainViewModel(settings, new FakeConnector(), dialogs, dialogs, () => TimeSpan.Zero);

            vm.Start();

            Assert.True(shown!.HasLoadError);
            Assert.Equal("Файл повреждён.", shown.LoadError);
        }

        [Fact]
        public void Main_ConnectionSaved_SessionApplied()
        {
            var dialogs = new FakeDialogs();
            var connector = new FakeConnector();
            dialogs.OnConnection = c =>
            {
                c.Server = "mail.example.test";
                c.UserName = "DOMAIN\\user";
                c.SetPassword("secret");
                c.Offset = c.OffsetOptions[0];
                c.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
                return c.OpenedSession != null;
            };
            var vm = new MainViewModel(new MemorySettings(), connector, dialogs, dialogs, () => TimeSpan.Zero);

            vm.Start();

            Assert.True(vm.Cleanup.HasSession);
            Assert.Equal("user@example.test на mail.example.test", vm.TargetLabel);
        }

        [Fact]
        public void Main_NewSession_DisposesPrevious()
        {
            // Утечка групп соединений и проверок сертификата при смене подключения недопустима.
            var dialogs = new FakeDialogs();
            var settings = new MemorySettings
            {
                Profile = new Core.Configuration.ConnectionProfile(
                    new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null),
                Credentials = new Core.Security.CredentialSecret("DOMAIN\\user", "secret"),
            };
            var connector = new FakeConnector();
            var vm = new MainViewModel(settings, connector, dialogs, dialogs, () => TimeSpan.Zero);
            vm.Start();
            var first = (TestSession)GetSession(vm);
            dialogs.OnConnection = c =>
            {
                c.SetPassword("secret");
                c.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
                return c.OpenedSession != null;
            };

            vm.EditConnectionCommand.Execute(null);

            Assert.True(first.IsDisposed);
            Assert.NotSame(first, GetSession(vm));
        }

        private static object GetSession(MainViewModel vm) =>
            typeof(MainViewModel).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;

        [Fact]
        public async System.Threading.Tasks.Task Period_OutOfSupportedRange_ValidationError_NoCrash()
        {
            // Аудит L3: 31.12.9999 давал переполнение и окно «непредвиденная ошибка».
            var vm = new CleanupViewModel(new FakeDialogs());
            vm.SetSession(new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>())));
            vm.IsPeriod = true;
            vm.PeriodStart = new DateTime(2026, 1, 1);
            vm.PeriodEnd = DateTime.MaxValue.Date;

            await vm.PreviewCommand.ExecuteAsync();

            Assert.Contains("1601", vm.ValidationError);
            Assert.False(vm.HasPreview);
        }

        [Fact]
        public async System.Threading.Tasks.Task Demo_PreviewOfLastMonth_FindsFlood()
        {
            var dialogs = new FakeDialogs();
            // Как в программе: демонстрационные данные и кнопка «последний месяц» отсчитываются от сегодняшнего дня.
            var demo = new DemoSession(DateTime.Today, TimeSpan.FromHours(3));
            var vm = new MainViewModel(demo, dialogs);
            vm.Cleanup.SetLastMonthCommand.Execute(null);

            await vm.Cleanup.PreviewCommand.ExecuteAsync();

            Assert.True(vm.IsDemo);
            Assert.True(vm.Cleanup.HasPreview, vm.Cleanup.Error?.Title);
            Assert.True(vm.Cleanup.SingleCount >= 2400);
            Assert.True(vm.Cleanup.InstanceCount > 0);
            Assert.False(vm.EditConnectionCommand.CanExecute(null));
        }
    }
}
