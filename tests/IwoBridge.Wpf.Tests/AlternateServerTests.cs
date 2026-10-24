using System;
using System.Linq;
using System.Threading.Tasks;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Transport;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Окно «Подключение» с резервным адресом: каждый адрес проверяется отдельно и со своим сертификатом; ошибка
    /// адреса, который ответил, не даёт сохранить; недоступность одного адреса — нормальна (внутренний вне офиса);
    /// доверие относится к тому адресу, чей сертификат показан. Адреса синтетические.
    /// </summary>
    public sealed class AlternateServerTests
    {
        private const string Internal = "int.example.test";
        private const string External = "ext.example.test";
        private const string PinInternal = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";
        private const string PinExternal = "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100";

        private static PresentedCertificate Certificate(string sha256) => new PresentedCertificate(
            "CN=" + External, "CN=" + External, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddYears(1),
            new CertificateFingerprint("A1B2C3D4E5F6071829303A4B5C6D7E8F90102030", sha256));

        private static ConnectionCheckResult Ok(string pin) =>
            ConnectionCheckResult.Success(5, CertificateDecision.AcceptFirstUse, Certificate(pin), pin);

        private static ConnectionCheckResult Unreachable() =>
            ConnectionCheckResult.Failure(ErrorMessages.For(OperationErrorKind.ConnectionReset), null, null);

        private static ConnectionCheckResult LoginRejected() =>
            ConnectionCheckResult.Failure(ErrorMessages.For(OperationErrorKind.AuthenticationFailed), CertificateDecision.AcceptPinned, Certificate(PinExternal));

        private static ConnectionViewModel Filled(FakeConnector connector, MemorySettings settings, FakeDialogs dialogs)
        {
            var vm = new ConnectionViewModel(settings, connector, dialogs, TimeSpan.FromHours(3), settings.Profile, settings.Credentials)
            {
                Server = Internal,
                AlternateServer = External,
                UserName = "DOMAIN\\user",
            };
            vm.SetPassword("secret");
            vm.Offset = vm.OffsetOptions[0];
            return vm;
        }

        [Fact]
        public async Task Check_EachAddressSeparately()
        {
            var connector = new FakeConnector();
            ConnectionViewModel vm = Filled(connector, new MemorySettings(), new FakeDialogs());

            await vm.CheckCommand.ExecuteAsync();

            Assert.Equal(new[] { Internal, External }, connector.Checked.Select(p => p.EwsUrl.Host));
            Assert.All(connector.Checked, p => Assert.Single(p.Endpoints));
            Assert.Equal("Подключение работает по обоим адресам.", vm.StatusTitle);
        }

        [Fact]
        public async Task PrimaryUnreachable_AlternateWorks_SavedWithAlternatePin_NoQuestion()
        {
            var connector = new FakeConnector { Respond = p => p.EwsUrl.Host == Internal ? Unreachable() : Ok(PinExternal) };
            var settings = new MemorySettings();
            var dialogs = new FakeDialogs();
            ConnectionViewModel vm = Filled(connector, settings, dialogs);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Empty(dialogs.Questions);
            Assert.Equal(1, settings.ProfileSaves);
            Assert.Equal(External, settings.Profile!.AlternateEwsUrl!.Host);
            Assert.Equal(PinExternal, settings.Profile.AlternatePinnedCertificateThumbprint);
            Assert.Null(settings.Profile.PinnedCertificateThumbprint); // сертификат недоступного адреса не известен
            Assert.NotNull(vm.OpenedSession);
        }

        [Fact]
        public async Task PrimaryUnreachable_StatusExplainsWhichWorks()
        {
            var connector = new FakeConnector { Respond = p => p.EwsUrl.Host == Internal ? Unreachable() : Ok(PinExternal) };
            ConnectionViewModel vm = Filled(connector, new MemorySettings(), new FakeDialogs());

            await vm.CheckCommand.ExecuteAsync();

            Assert.True(vm.StatusIsSuccess);
            Assert.Equal("Подключение работает по резервному адресу.", vm.StatusTitle);
            Assert.Contains("Основной адрес сейчас недоступен", vm.StatusHint);
        }

        [Fact]
        public async Task AlternateAnswersWithError_NotSaved_ErrorNamesAddress()
        {
            var connector = new FakeConnector { Respond = p => p.EwsUrl.Host == Internal ? Ok(PinInternal) : LoginRejected() };
            var settings = new MemorySettings();
            ConnectionViewModel vm = Filled(connector, settings, new FakeDialogs());

            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(0, settings.ProfileSaves);
            Assert.False(vm.StatusIsSuccess);
            Assert.StartsWith("Резервный адрес: ", vm.StatusTitle);
        }

        [Fact]
        public async Task BothUnreachable_AsksToSaveWithoutCheck()
        {
            var connector = new FakeConnector { Respond = _ => Unreachable() };
            var settings = new MemorySettings();
            var dialogs = new FakeDialogs { AskAnswer = true };
            ConnectionViewModel vm = Filled(connector, settings, dialogs);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Single(dialogs.Questions);
            Assert.Equal(1, settings.ProfileSaves);
            Assert.StartsWith("Оба адреса: ", vm.StatusTitle);
        }

        [Fact]
        public async Task AlternateSameAsPrimary_ErrorUnderAlternate_NothingChecked()
        {
            var connector = new FakeConnector();
            ConnectionViewModel vm = Filled(connector, new MemorySettings(), new FakeDialogs());
            vm.AlternateServer = "https://" + Internal + "/EWS/Exchange.asmx";

            await vm.CheckCommand.ExecuteAsync();

            Assert.Contains("совпадает с основным", vm.AlternateServerError);
            Assert.Empty(connector.Checked);
        }

        [Fact]
        public async Task BadAlternate_ErrorUnderAlternate()
        {
            var connector = new FakeConnector();
            ConnectionViewModel vm = Filled(connector, new MemorySettings(), new FakeDialogs());
            vm.AlternateServer = "http://" + External;

            await vm.CheckCommand.ExecuteAsync();

            Assert.NotNull(vm.AlternateServerError);
            Assert.Null(vm.ServerError);
            Assert.Empty(connector.Checked);
        }

        [Fact]
        public async Task TrustUntrustedAlternate_PinGoesToAlternateOnly()
        {
            var connector = new FakeConnector
            {
                Respond = p => p.EwsUrl.Host == Internal
                    ? Ok(PinInternal)
                    : p.PinnedCertificateThumbprint == PinExternal
                        ? Ok(PinExternal)
                        : ConnectionCheckResult.Failure(
                            ErrorMessages.For(OperationErrorKind.CertificatePinMismatch), CertificateDecision.RejectUntrusted, Certificate(PinExternal)),
            };
            var settings = new MemorySettings();
            ConnectionViewModel vm = Filled(connector, settings, new FakeDialogs());

            await vm.CheckCommand.ExecuteAsync();
            Assert.True(vm.CanTrustCertificate);
            vm.TrustCertificateCommand.Execute(null);
            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(1, settings.ProfileSaves);
            Assert.Equal(PinInternal, settings.Profile!.PinnedCertificateThumbprint);
            Assert.Equal(PinExternal, settings.Profile.AlternatePinnedCertificateThumbprint);
        }

        [Fact]
        public void SavedAlternate_Prefilled_TrustShownPerAddress()
        {
            var settings = new MemorySettings
            {
                Profile = new Core.Configuration.ConnectionProfile(
                    new Uri("https://" + Internal + "/EWS/Exchange.asmx"), null, TimeSpan.FromHours(3), PinInternal,
                    new Uri("https://" + External + "/EWS/Exchange.asmx"), null),
            };

            var vm = new ConnectionViewModel(settings, new FakeConnector(), new FakeDialogs(), TimeSpan.FromHours(3), settings.Profile, null);

            Assert.Equal(External, vm.AlternateServer);
            Assert.Contains("Основной адрес: доверие сертификату 0011", vm.TrustText);
            Assert.Contains("Резервный адрес: сертификат будет запомнен", vm.TrustText);
        }

        [Fact]
        public void ClearingAlternate_TrustTextBackToSingle()
        {
            var settings = new MemorySettings
            {
                Profile = new Core.Configuration.ConnectionProfile(
                    new Uri("https://" + Internal + "/EWS/Exchange.asmx"), null, TimeSpan.FromHours(3), PinInternal,
                    new Uri("https://" + External + "/EWS/Exchange.asmx"), PinExternal),
            };
            var vm = new ConnectionViewModel(settings, new FakeConnector(), new FakeDialogs(), TimeSpan.FromHours(3), settings.Profile, null);

            vm.AlternateServer = string.Empty;

            Assert.StartsWith("Программа доверяет сертификату с отпечатком 0011", vm.TrustText);
        }
    }
}
