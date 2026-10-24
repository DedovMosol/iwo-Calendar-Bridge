using System;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Core.Transport;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Окно «Подключение»: поля, ошибки под полями, проверка перед сохранением, доверие к сертификату, сохранение
    /// без проверки для недоступного сервера (внутренний адрес вне сети). Данные синтетические.
    /// </summary>
    public sealed class ConnectionViewModelTests
    {
        private const string Sha1 = "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string Sha256 = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

        private static readonly TimeSpan Plus3 = TimeSpan.FromHours(3);

        private static PresentedCertificate Certificate() => new PresentedCertificate(
            "CN=mail.example.test", "CN=mail.example.test", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddYears(1),
            new CertificateFingerprint(Sha1, Sha256));

        private static ConnectionViewModel Create(
            FakeConnector connector, MemorySettings settings, FakeDialogs? dialogs = null)
        {
            return new ConnectionViewModel(
                settings, connector, dialogs ?? new FakeDialogs(), Plus3, settings.Profile, settings.Credentials);
        }

        private static ConnectionViewModel Filled(FakeConnector connector, MemorySettings settings, FakeDialogs? dialogs = null)
        {
            ConnectionViewModel vm = Create(connector, settings, dialogs);
            vm.Server = "mail.example.test";
            vm.UserName = "DOMAIN\\user";
            vm.SetPassword("secret");
            vm.Offset = vm.OffsetOptions[0];
            return vm;
        }

        [Fact]
        public void SavedProfile_PrefillsFields_ShortServer()
        {
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), "box@example.test", Plus3, Sha256),
                Credentials = new CredentialSecret("DOMAIN\\user", "secret"),
            };

            ConnectionViewModel vm = Create(new FakeConnector(), settings);

            Assert.Equal("mail.example.test", vm.Server);
            Assert.Equal("box@example.test", vm.Mailbox);
            Assert.Equal("DOMAIN\\user", vm.UserName);
            Assert.Equal(Plus3, vm.Offset!.Offset);
            Assert.Contains("0011 2233", vm.TrustText);
            Assert.Contains("оставьте поле пустым", vm.PasswordHint, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Save_EmptyFields_ShowsErrorsUnderFields_NothingSaved()
        {
            var connector = new FakeConnector();
            var settings = new MemorySettings();
            ConnectionViewModel vm = Create(connector, settings);

            await vm.SaveCommand.ExecuteAsync();

            Assert.NotNull(vm.ServerError);
            Assert.NotNull(vm.UserError);
            Assert.NotNull(vm.PasswordError);
            Assert.NotNull(vm.OffsetError);
            Assert.Empty(connector.Checked);
            Assert.Equal(0, settings.ProfileSaves);
        }

        [Fact]
        public async Task Save_HttpServer_Rejected()
        {
            var connector = new FakeConnector();
            ConnectionViewModel vm = Filled(connector, new MemorySettings());
            vm.Server = "http://mail.example.test";

            await vm.SaveCommand.ExecuteAsync();

            Assert.Contains("https://", vm.ServerError);
            Assert.Empty(connector.Checked);
        }

        [Fact]
        public async Task Save_BadMailbox_ErrorUnderMailbox()
        {
            ConnectionViewModel vm = Filled(new FakeConnector(), new MemorySettings());
            vm.Mailbox = "not-an-address";

            await vm.SaveCommand.ExecuteAsync();

            Assert.NotNull(vm.MailboxError);
        }

        [Fact]
        public async Task Save_CheckSucceeds_SavesProfileWithFirstUsePin_AndCredentials_OpensSession()
        {
            var connector = new FakeConnector
            {
                Respond = _ => ConnectionCheckResult.Success(42, CertificateDecision.AcceptFirstUse, Certificate(), Sha256),
            };
            var settings = new MemorySettings();
            ConnectionViewModel vm = Filled(connector, settings);
            vm.Server = "10.0.0.5:8443";
            bool saved = false;
            vm.Saved += (_, __) => saved = true;

            await vm.SaveCommand.ExecuteAsync();

            Assert.True(saved);
            Assert.Equal(new Uri("https://10.0.0.5:8443/EWS/Exchange.asmx"), settings.Profile!.EwsUrl);
            Assert.Equal(Sha256, settings.Profile.PinnedCertificateThumbprint);
            Assert.Equal("DOMAIN\\user", settings.Credentials!.UserName);
            Assert.Equal("secret", settings.Credentials.Password);
            Assert.NotNull(vm.OpenedSession);
            Assert.Equal(Sha256, connector.OpenedProfile!.PinnedCertificateThumbprint);
        }

        [Fact]
        public async Task Save_BlankPassword_SameUser_KeepsSavedPassword()
        {
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, Plus3, null),
                Credentials = new CredentialSecret("DOMAIN\\user", "old-secret"),
            };
            var connector = new FakeConnector();
            ConnectionViewModel vm = Create(connector, settings);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Null(vm.PasswordError);
            Assert.Equal("old-secret", settings.Credentials!.Password);
        }

        [Fact]
        public async Task Save_BlankPassword_OtherUser_PasswordRequired()
        {
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, Plus3, null),
                Credentials = new CredentialSecret("DOMAIN\\user", "old-secret"),
            };
            ConnectionViewModel vm = Create(new FakeConnector(), settings);
            vm.UserName = "DOMAIN\\other";

            await vm.SaveCommand.ExecuteAsync();

            Assert.NotNull(vm.PasswordError);
            Assert.Equal(0, settings.ProfileSaves);
        }

        [Fact]
        public async Task UntrustedCertificate_ShownAndTrusted_NextCheckUsesPin()
        {
            var connector = new FakeConnector
            {
                Respond = p => p.PinnedCertificateThumbprint is null
                    ? ConnectionCheckResult.Failure(
                        ErrorMessages.For(Core.Errors.OperationErrorKind.CertificatePinMismatch), CertificateDecision.RejectUntrusted, Certificate())
                    : ConnectionCheckResult.Success(5, CertificateDecision.AcceptPinned, Certificate(), p.PinnedCertificateThumbprint),
            };
            var settings = new MemorySettings();
            ConnectionViewModel vm = Filled(connector, settings);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(0, settings.ProfileSaves);
            Assert.True(vm.HasCertificate);
            Assert.True(vm.CanTrustCertificate);
            Assert.Equal("Самоподписанный (выдан сам себе)", vm.CertificateIssuer);
            Assert.StartsWith("0011 2233 4455", vm.CertificateSha256);
            Assert.Equal("Windows не доверяет сертификату сервера.", vm.StatusTitle);

            vm.TrustCertificateCommand.Execute(null);
            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(Sha256, connector.Checked[1].PinnedCertificateThumbprint);
            Assert.Equal(Sha256, settings.Profile!.PinnedCertificateThumbprint);
        }

        [Fact]
        public async Task Interception_CannotBeTrusted()
        {
            var connector = new FakeConnector
            {
                Respond = _ => ConnectionCheckResult.Failure(
                    ErrorMessages.For(Core.Errors.OperationErrorKind.CertificatePinMismatch), CertificateDecision.RejectInterception, Certificate()),
            };
            ConnectionViewModel vm = Filled(connector, new MemorySettings());

            await vm.CheckCommand.ExecuteAsync();

            Assert.False(vm.CanTrustCertificate);
            Assert.Contains("антивирус", vm.StatusTitle, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]
        public async Task UnreachableServer_AsksToSaveWithoutCheck(bool answer, int expectedSaves)
        {
            var connector = new FakeConnector
            {
                Respond = _ => ConnectionCheckResult.Failure(
                    ErrorMessages.For(Core.Errors.OperationErrorKind.ConnectionReset), null, null),
            };
            var settings = new MemorySettings();
            var dialogs = new FakeDialogs { AskAnswer = answer };
            ConnectionViewModel vm = Filled(connector, settings, dialogs);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Single(dialogs.Questions);
            Assert.Equal(expectedSaves, settings.ProfileSaves);
        }

        [Fact]
        public async Task AuthenticationFailure_NotSaved_NoQuestion()
        {
            var connector = new FakeConnector
            {
                Respond = _ => ConnectionCheckResult.Failure(
                    ErrorMessages.For(Core.Errors.OperationErrorKind.AuthenticationFailed), CertificateDecision.AcceptPinned, Certificate()),
            };
            var settings = new MemorySettings();
            var dialogs = new FakeDialogs { AskAnswer = true };
            ConnectionViewModel vm = Filled(connector, settings, dialogs);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Empty(dialogs.Questions);
            Assert.Equal(0, settings.ProfileSaves);
            Assert.Equal("Неверное имя пользователя или пароль.", vm.StatusTitle);
        }

        [Fact]
        public void ChangingServer_DropsPinOfPreviousServer()
        {
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, Plus3, Sha256),
                Credentials = new CredentialSecret("DOMAIN\\user", "secret"),
            };
            ConnectionViewModel vm = Create(new FakeConnector(), settings);

            vm.Server = "external.example.test";

            Assert.StartsWith("Не задан", vm.TrustText);
        }

        [Fact]
        public void SameServerOtherSpelling_KeepsPin()
        {
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, Plus3, Sha256),
                Credentials = new CredentialSecret("DOMAIN\\user", "secret"),
            };
            ConnectionViewModel vm = Create(new FakeConnector(), settings);

            vm.Server = "https://MAIL.example.test/EWS/Exchange.asmx";

            Assert.Contains("0011", vm.TrustText);
        }

        [Fact]
        public async Task SuccessfulCheck_ShowsItemCount()
        {
            var connector = new FakeConnector { Respond = _ => ConnectionCheckResult.Success(1234, CertificateDecision.AcceptPinned, Certificate(), Sha256) };
            ConnectionViewModel vm = Filled(connector, new MemorySettings());

            await vm.CheckCommand.ExecuteAsync();

            Assert.True(vm.StatusIsSuccess);
            Assert.Contains("1\u00A0234 элемента", vm.StatusHint);
        }

        [Fact]
        public async Task ClosedDuringCheck_NothingSaved()
        {
            // Аудит M1: «Отмена» во время проверки — ответ сервера пришёл позже, но настройки не записываются.
            var settings = new MemorySettings();
            ConnectionViewModel? vm = null;
            var connector = new FakeConnector
            {
                Respond = _ =>
                {
                    vm!.Close();
                    return ConnectionCheckResult.Success(1, CertificateDecision.AcceptPinned, Certificate(), Sha256);
                },
            };
            vm = Filled(connector, settings);

            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(0, settings.ProfileSaves);
            Assert.Null(settings.Credentials);
            Assert.Null(vm.OpenedSession);
        }

        [Fact]
        public void TypoInServerAndBack_RestoresSavedPin()
        {
            // Аудит M2a: случайная опечатка в адресе не должна стирать закрепление.
            var settings = new MemorySettings
            {
                Profile = new ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, Plus3, Sha256),
                Credentials = new CredentialSecret("DOMAIN\\user", "secret"),
            };
            ConnectionViewModel vm = Create(new FakeConnector(), settings);

            vm.Server = "mail.example.tes";
            Assert.False(vm.HasPinnedCertificate);
            vm.Server = "mail.example.test";

            Assert.True(vm.HasPinnedCertificate);
            Assert.Contains("0011", vm.TrustText);
        }

        [Fact]
        public async Task TrustedCertOfOneServer_NotAppliedToAnother()
        {
            // Аудит M2b: доверие, выданное серверу A, не переносится на сервер C.
            var connector = new FakeConnector
            {
                Respond = _ => ConnectionCheckResult.Failure(
                    ErrorMessages.For(Core.Errors.OperationErrorKind.CertificatePinMismatch), CertificateDecision.RejectUntrusted, Certificate()),
            };
            ConnectionViewModel vm = Filled(connector, new MemorySettings());
            await vm.CheckCommand.ExecuteAsync();
            vm.TrustCertificateCommand.Execute(null);
            Assert.True(vm.HasPinnedCertificate);

            vm.Server = "other.example.test";
            Assert.False(vm.HasPinnedCertificate);

            vm.Server = "mail.example.test";
            Assert.True(vm.HasPinnedCertificate);
        }

        [Fact]
        public async Task CredentialStoreFails_ProfileNotWritten()
        {
            // Аудит M3: сначала пароль, потом файл — при сбое диспетчера на диске не окажется нового сервера.
            var settings = new FailingCredentialsSettings();
            ConnectionViewModel vm = new ConnectionViewModel(
                settings, new FakeConnector(), new FakeDialogs(), Plus3, null, null);
            vm.Server = "mail.example.test";
            vm.UserName = "DOMAIN\\user";
            vm.SetPassword("secret");
            vm.Offset = vm.OffsetOptions[0];

            await vm.SaveCommand.ExecuteAsync();

            Assert.Equal(0, settings.ProfileSaves);
            Assert.Equal("Настройки не сохранены.", vm.StatusTitle);
        }

        [Fact]
        public async Task ConnectorThrows_ShownAsError_WindowUsable()
        {
            var vm = new ConnectionViewModel(
                new MemorySettings(), new ThrowingConnector(), new FakeDialogs(), Plus3, null, null);
            vm.Server = "mail.example.test";
            vm.UserName = "DOMAIN\\user";
            vm.SetPassword("secret");
            vm.Offset = vm.OffsetOptions[0];

            await vm.CheckCommand.ExecuteAsync();

            Assert.False(vm.IsChecking);
            Assert.False(vm.StatusIsSuccess);
            Assert.Equal("сбой", vm.StatusDetails);
            Assert.True(vm.CheckCommand.CanExecute(null));
        }

        private sealed class FailingCredentialsSettings : ISettingsStore
        {
            public int ProfileSaves { get; private set; }

            public ConnectionProfile? LoadProfile() => null;

            public void SaveProfile(ConnectionProfile profile) => ProfileSaves++;

            public CredentialSecret? LoadCredentials() => null;

            public void SaveCredentials(CredentialSecret credentials) =>
                throw new System.ComponentModel.Win32Exception(1312, "нет сеанса входа");
        }

        private sealed class ThrowingConnector : ITargetConnector
        {
            public Task<ConnectionCheckResult> CheckAsync(ConnectionProfile profile, CredentialSecret credentials, System.Threading.CancellationToken cancellationToken) =>
                throw new InvalidOperationException("сбой");

            public ITargetSession Open(ConnectionProfile profile, CredentialSecret credentials) => throw new InvalidOperationException("сбой");
        }

        [Fact]
        public void LocalOffset_MarkedInList()
        {
            ConnectionViewModel vm = Create(new FakeConnector(), new MemorySettings());

            Assert.Contains(vm.OffsetOptions, o => o.Offset == Plus3 && o.Label.Contains("сейчас на этом компьютере"));
            Assert.Null(vm.Offset);
        }
    }
}
