using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Wpf.Mvvm;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Для чего учётная запись.</summary>
    public enum AccountRole
    {
        /// <summary>Куда копировать (и что очищать).</summary>
        Target,

        /// <summary>Откуда копировать по EWS, если не из Outlook.</summary>
        Source,

        /// <summary>Ящик, добавленный для очистки.</summary>
        Mailbox,
    }

    /// <summary>Строка окна «Учётные записи»: одно подключение по EWS.</summary>
    public sealed class AccountRow
    {
        internal AccountRow(
            AccountRole role, bool isConfigured, string mailbox, string server, string userName, string password, bool hasSavedPassword,
            CleanupChoice? choice = null)
        {
            Role = role;
            IsConfigured = isConfigured;
            Mailbox = mailbox;
            Server = server;
            UserName = userName;
            Password = password;
            HasSavedPassword = hasSavedPassword;
            Choice = choice;
        }

        public AccountRole Role { get; }

        public string RoleText => RoleName(Role);

        /// <summary>Подключение задано (профиль сохранён или открыто до закрытия программы).</summary>
        public bool IsConfigured { get; }

        public string Mailbox { get; }

        public string Server { get; }

        public string UserName { get; }

        /// <summary>Где пароль: «сохранён в Windows», «до закрытия программы», «не введён».</summary>
        public string Password { get; }

        /// <summary>Пароль лежит в диспетчере учётных данных Windows — его можно забыть.</summary>
        public bool HasSavedPassword { get; }

        /// <summary>Добавленная почта — строка списка очистки; у приёмника и источника — <c>null</c>.</summary>
        internal CleanupChoice? Choice { get; }

        internal static string RoleName(AccountRole role) =>
            role == AccountRole.Target ? "Приёмник" : role == AccountRole.Source ? "Источник по EWS" : "Почта для очистки";
    }

    /// <summary>Итог действия с учётной записью — строкой под списком.</summary>
    internal sealed class AccountOutcome
    {
        private AccountOutcome(string text, bool isError)
        {
            Text = text;
            IsError = isError;
        }

        public string Text { get; }

        public bool IsError { get; }

        public static AccountOutcome Done(string text) => new AccountOutcome(text, false);

        public static AccountOutcome Failed(string text) => new AccountOutcome(text, true);
    }

    /// <summary>
    /// Учётные записи программы — их список и действия с ними. Реализует главное окно (<see cref="MainViewModel"/>):
    /// оно владеет подключениями. Действие вернуло <c>null</c> — человек отказался, ничего не изменилось.
    /// </summary>
    internal interface IAccountManager
    {
        /// <summary>Менять можно: не демонстрационный режим и не идёт операция.</summary>
        bool CanChangeAccounts { get; }

        bool CanAddMailbox { get; }

        IReadOnlyList<AccountRow> Accounts();

        AccountOutcome? AddMailbox();

        AccountOutcome? Edit(AccountRow account);

        AccountOutcome? ForgetPassword(AccountRow account);

        AccountOutcome? Delete(AccountRow account);
    }

    /// <summary>
    /// Окно «Учётные записи»: все подключения по EWS в одном списке — приёмник, источник по EWS, добавленная почта.
    /// Для выбранной строки: изменить (или настроить, если не задано), забыть пароль (удалить из Windows, подключение
    /// работает до закрытия программы), удалить (настройки и пароль). Добавить — ещё один ящик для очистки. Календари
    /// ящиков при этом не меняются. После каждого действия список перечитывается, выбранной остаётся та же строка.
    /// </summary>
    public sealed class AccountsViewModel : ObservableObject
    {
        private readonly IAccountManager _accounts;
        private IReadOnlyList<AccountRow> _rows = Array.Empty<AccountRow>();
        private AccountRow? _selected;
        private string? _status;
        private bool _statusIsError;

        internal AccountsViewModel(IAccountManager accounts)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            AddCommand = new RelayCommand(Add, () => _accounts.CanChangeAccounts && _accounts.CanAddMailbox);
            EditCommand = new RelayCommand(() => Run(_accounts.Edit), () => _accounts.CanChangeAccounts && _selected != null);
            ForgetPasswordCommand = new RelayCommand(
                () => Run(_accounts.ForgetPassword), () => _accounts.CanChangeAccounts && _selected?.HasSavedPassword == true);
            DeleteCommand = new RelayCommand(() => Run(_accounts.Delete), () => _accounts.CanChangeAccounts && _selected?.IsConfigured == true);
            Reload(0);
        }

        public IReadOnlyList<AccountRow> Rows
        {
            get => _rows;
            private set => SetProperty(ref _rows, value);
        }

        public AccountRow? Selected
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value))
                {
                    OnPropertyChanged(nameof(EditText));
                    RaiseCommands();
                }
            }
        }

        public RelayCommand AddCommand { get; }

        public RelayCommand EditCommand { get; }

        public RelayCommand ForgetPasswordCommand { get; }

        public RelayCommand DeleteCommand { get; }

        /// <summary>Подпись кнопки: не заданное подключение настраивается, заданное — меняется.</summary>
        public string EditText => _selected?.IsConfigured == false ? "_Настроить…" : "_Изменить…";

        /// <summary>Итог последнего действия; <c>null</c> — действий не было.</summary>
        public string? Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetProperty(ref _statusIsError, value);
        }

        /// <summary>Почему менять нельзя (демонстрационный режим, идёт операция); <c>null</c> — можно.</summary>
        public string? ReadOnlyReason => _accounts.CanChangeAccounts
            ? null
            : "Сейчас учётные записи только просматриваются: в демонстрационном режиме и во время операции они не меняются.";

        private void Add()
        {
            int before = _rows.Count;
            AccountOutcome? outcome = _accounts.AddMailbox();
            Reload(-1);
            Selected = _rows.Count > before ? _rows.Last() : Selected;
            Show(outcome);
        }

        private void Run(Func<AccountRow, AccountOutcome?> action)
        {
            AccountRow? row = _selected;
            if (row is null)
            {
                return;
            }

            int index = IndexOf(row);
            AccountOutcome? outcome = action(row);
            Reload(index);
            Show(outcome);
        }

        /// <summary>Список заново; выбрана строка с тем же номером (действия не меняют порядок), иначе последняя.</summary>
        private void Reload(int index)
        {
            // Прежний выбор — до смены списка: таблица окна, получив новый список, сама сбрасывает выбранную строку.
            AccountRow? previous = _selected;
            Rows = _accounts.Accounts();
            if (index >= 0 || previous is null)
            {
                Selected = _rows.Count == 0 ? null : _rows[Math.Min(Math.Max(index, 0), _rows.Count - 1)];
            }
            else
            {
                int same = IndexOf(previous);
                Selected = same >= 0 ? _rows[same] : _rows.FirstOrDefault();
            }

            OnPropertyChanged(nameof(ReadOnlyReason));
            RaiseCommands();
        }

        private int IndexOf(AccountRow row)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (ReferenceEquals(_rows[i], row) || (_rows[i].Role == row.Role && row.Role != AccountRole.Mailbox)
                    || (row.Choice != null && ReferenceEquals(_rows[i].Choice, row.Choice)))
                {
                    return i;
                }
            }

            return -1;
        }

        private void Show(AccountOutcome? outcome)
        {
            if (outcome is null)
            {
                return;
            }

            StatusIsError = outcome.IsError;
            Status = outcome.Text;
        }

        private void RaiseCommands()
        {
            AddCommand.RaiseCanExecuteChanged();
            EditCommand.RaiseCanExecuteChanged();
            ForgetPasswordCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }
}
