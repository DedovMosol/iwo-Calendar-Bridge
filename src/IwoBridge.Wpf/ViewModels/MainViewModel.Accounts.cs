using System;
using System.Collections.Generic;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Wpf.Services;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Окно «Учётные записи» со стороны главного окна: список подключений и действия с ними. Здесь, а не в модели окна,
    /// потому что подключениями (сеансами приёмника, источника и добавленной почты) владеет главное окно — после
    /// изменения оно же переключает копирование и список очистки.
    /// </summary>
    public sealed partial class MainViewModel : IAccountManager
    {
        private const string NoTarget = "Приёмник не настроен";

        bool IAccountManager.CanChangeAccounts => _settings != null && !IsBusy;

        bool IAccountManager.CanAddMailbox => _mailboxStores != null;

        IReadOnlyList<AccountRow> IAccountManager.Accounts()
        {
            var rows = new List<AccountRow>();
            if (_settings is null)
            {
                // Демонстрационный режим: один сеанс в памяти — и приёмник, и источник по EWS.
                if (_session != null)
                {
                    rows.Add(new AccountRow(AccountRole.Target, true, _session.TargetLabel, "в памяти, без сети", "—", "не нужен", false));
                    rows.Add(new AccountRow(AccountRole.Source, true, _session.TargetLabel, "в памяти, без сети", "—", "не нужен", false));
                }

                return rows;
            }

            rows.Add(Row(AccountRole.Target, _settings, _session, null, null, null));
            if (_sourceSettings != null)
            {
                rows.Add(Row(AccountRole.Source, _sourceSettings, _sourceSession, null, null, null));
            }

            foreach (CleanupChoice added in _added)
            {
                rows.Add(Row(AccountRole.Mailbox, added.Store, added.Session, added.Profile, added.UserName, added));
            }

            return rows;
        }

        AccountOutcome? IAccountManager.AddMailbox() => _mailboxStores is null || IsBusy ? null : AddMailbox();

        AccountOutcome? IAccountManager.Edit(AccountRow account)
        {
            if (_settings is null || IsBusy)
            {
                return null;
            }

            bool saved = account.Role == AccountRole.Target ? EditTarget()
                : account.Role == AccountRole.Source ? _sourceSettings != null && EditSource()
                : EditMailbox(account.Choice);
            return saved ? AccountOutcome.Done("Подключение сохранено.") : null;
        }

        AccountOutcome? IAccountManager.ForgetPassword(AccountRow account)
        {
            ISettingsStore? store = StoreOf(account);
            if (store is null || IsBusy)
            {
                return null;
            }

            string question = "Пароль учётной записи " + account.UserName + " (" + Describe(account) + ") будет удалён из диспетчера учётных данных Windows."
                + "\n\nДо закрытия программы подключение работает. При следующем запуске программа спросит пароль"
                + (account.Role == AccountRole.Mailbox ? ", а до тех пор почта будет в списке с пометкой «нужен пароль»." : ".");
            if (!_dialogs.Ask("Забыть пароль?", question, "Забыть пароль") || IsBusy)
            {
                return null;
            }

            try
            {
                // Сначала профиль с именем учётной записи, потом удаление пароля: при сбое удаления имя не теряется.
                ConnectionProfile? profile = store.LoadProfile();
                CredentialSecret? credentials = store.LoadCredentials();
                if (profile != null && credentials != null)
                {
                    store.SaveProfile(profile, credentials.UserName);
                }

                store.DeleteCredentials();
            }
#pragma warning disable CA1031 // Файл занят, нет прав, диспетчер учётных данных недоступен — сообщается под списком.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return AccountOutcome.Failed("Пароль не удалён: " + ex.Message);
            }

            return Changed(
                "пароль удалён из Windows — " + Describe(account),
                "Пароль удалён из Windows. До закрытия программы подключение работает, при следующем запуске программа спросит пароль.");
        }

        AccountOutcome? IAccountManager.Delete(AccountRow account)
        {
            if (_settings is null || IsBusy || !account.IsConfigured)
            {
                return null;
            }

            switch (account.Role)
            {
                case AccountRole.Target:
                    return DeleteConnection(
                        _settings, account, "Копировать будет некуда, пока приёмник не настроен снова.",
                        () =>
                        {
                            Copy.SetTarget(null);
                            ITargetSession? previous = _session;
                            _session = null;
                            TargetLabel = NoTarget;
                            RebuildCleanupChoices();
                            previous?.Dispose();
                        });
                case AccountRole.Source when _sourceSettings != null:
                    return DeleteConnection(
                        _sourceSettings, account, "Копировать с сервера по EWS будет нельзя, пока источник не настроен снова.",
                        () =>
                        {
                            Copy.SetEwsSource(null);
                            ITargetSession? previous = _sourceSession;
                            _sourceSession = null;
                            RebuildCleanupChoices();
                            previous?.Dispose();
                        });
                case AccountRole.Mailbox when account.Choice != null:
                    return RemoveMailbox(account.Choice);
                default:
                    return null;
            }
        }

        /// <summary>Удалить приёмник или источник: подтверждение, настройки и пароль, затем отключение в окне.</summary>
        private AccountOutcome? DeleteConnection(ISettingsStore store, AccountRow account, string consequence, Action detach)
        {
            string what = account.Role == AccountRole.Target ? "приёмника" : "источника по EWS";
            string question = "Будут удалены настройки подключения " + what + " (" + Describe(account) + ") и сохранённый пароль. "
                + consequence + "\n\nСобытия в календаре ящика не меняются.";
            if (!_dialogs.Ask("Удалить подключение " + what + "?", question, "Удалить") || IsBusy)
            {
                return null;
            }

            if (!TryDelete(store, out string? error))
            {
                return AccountOutcome.Failed("Подключение не удалено: " + error + " Проверьте права на папку программы и попробуйте ещё раз.");
            }

            detach();
            return Changed("удалено подключение " + what + " — " + Describe(account), "Подключение " + what + " удалено.");
        }

        private ISettingsStore? StoreOf(AccountRow account) =>
            account.Role == AccountRole.Target ? _settings
            : account.Role == AccountRole.Source ? _sourceSettings
            : account.Choice?.Store;

        /// <summary>Изменение учётных записей — в консоль (журнал операций его не хранит) и строкой под списком.</summary>
        private AccountOutcome Changed(string logText, string status)
        {
            _log?.Info("Учётные записи: " + logText);
            return AccountOutcome.Done(status);
        }

        private static string Describe(AccountRow account) =>
            account.RoleText.ToLowerInvariant() + " " + account.Mailbox + (account.Server.Length == 0 ? string.Empty : " на " + account.Server);

        /// <summary>
        /// Строка списка по сохранённым настройкам и открытому сеансу. Пароль: в диспетчере учётных данных Windows, только в
        /// памяти до закрытия программы или не введён (подключения нет).
        /// </summary>
        private static AccountRow Row(
            AccountRole role, ISettingsStore? store, ITargetSession? session, ConnectionProfile? known, string? knownUser, CleanupChoice? choice)
        {
            ConnectionProfile? profile = known;
            CredentialSecret? credentials = null;
            string? error = null;
            if (store != null)
            {
                error = LoadSaved(store, out ConnectionProfile? saved, out credentials);
                profile = saved ?? profile;
            }

            string user = credentials?.UserName ?? (store is null ? null : TryUserName(store)) ?? knownUser ?? "—";
            if (error != null)
            {
                return new AccountRow(
                    role, true, profile?.MailboxAddress ?? "настройки не читаются", profile is null ? string.Empty : Server(profile), user,
                    "не прочитан: " + error, false, choice);
            }

            if (profile is null && session is null)
            {
                return new AccountRow(role, false, "не настроен", string.Empty, string.Empty, string.Empty, false, choice);
            }

            string mailbox = profile is null ? session!.TargetLabel : profile.MailboxAddress ?? "ящик учётной записи";
            string password = credentials != null ? "сохранён в Windows" : session != null ? "до закрытия программы" : "не введён";
            return new AccountRow(role, true, mailbox, profile is null ? string.Empty : Server(profile), user, password, credentials != null, choice);
        }

        private static string Server(ConnectionProfile profile) =>
            ServerAddress.ToDisplay(profile.EwsUrl)
            + (profile.AlternateEwsUrl is null ? string.Empty : ", резервный " + ServerAddress.ToDisplay(profile.AlternateEwsUrl));

        private static string? TryUserName(ISettingsStore store)
        {
            try
            {
                return store.LoadUserName();
            }
#pragma warning disable CA1031 // Имя не прочиталось — в списке прочерк, остальное работает.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }
    }
}
