using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Строка предпросмотра копирования — одно действие, которое человек может снять или оставить.</summary>
    public sealed class CopyRow : ObservableObject
    {
        private bool _isSelected = true;

        internal CopyRow(SyncAction action, string kind, string subject, string start, string reason)
        {
            Action = action;
            Kind = kind;
            Subject = subject;
            Start = start;
            Reason = reason;
        }

        internal SyncAction Action { get; }

        /// <summary>Что будет сделано: «Создать», «Перезаписать», «Удалить копию»…</summary>
        public string Kind { get; }

        public string Subject { get; }

        public string Start { get; }

        public string Reason { get; }

        public bool IsDelete => Action.Kind == SyncActionKind.Delete;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>Вариант для личных событий в списке.</summary>
    public sealed class PrivacyOption
    {
        internal PrivacyOption(PrivateEvents value, string label)
        {
            Value = value;
            Label = label;
        }

        public PrivateEvents Value { get; }

        public string Label { get; }
    }

    /// <summary>Откуда копировать.</summary>
    public enum SourceKind
    {
        /// <summary>Календарь Outlook этого компьютера.</summary>
        Outlook,

        /// <summary>Сервер Exchange по EWS (отдельное подключение «Источник»).</summary>
        Ews,
    }

    /// <summary>
    /// Экран копирования «откуда → куда»: источник (Outlook этого компьютера или сервер по EWS) → приёмник (подключение
    /// по EWS) за период. Предпросмотр показывает каждое действие (создать, перезаписать, удалить копию) с флажком —
    /// человек выбирает, что выполнить. Затем подтверждение, ход работы с остановкой и итог со сбоями по событиям.
    ///
    /// Правила, которые держит модель:
    ///  - выполнить можно только предпросмотр, построенный для текущих источника, приёмника и периода — их смена
    ///    сбрасывает его; после выполнения он израсходован;
    ///  - трогаются только копии моста; события приёмника без метки не видны и не меняются (ядро);
    ///  - массовое удаление копий — только после явной отметки «подтверждаю» и нового предпросмотра.
    /// </summary>
    public sealed class CopyViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;
        private readonly IOutlookCalendars? _outlook;
        private readonly Func<DateTime> _today;

        private ITargetSession? _target;
        private ITargetSession? _ewsSource;
        private SourceKind _sourceKind;
        private IReadOnlyList<SourceChoice> _outlookCalendars = Array.Empty<SourceChoice>();
        private SourceChoice? _outlookCalendar;
        private string? _outlookError;
        private bool _isLoadingOutlook;
        private string _calendarSearch = string.Empty;
        private readonly List<SourceChoice> _foundShared = new List<SourceChoice>();
        private readonly ICopySettingsStore? _settingsStore;
        private string? _savedCalendar;
        private string? _savedCalendarKey;
        private int _daysBack;
        private int _daysForward;
        private readonly Func<bool> _otherBusy;
        private string? _savedOwner;
        private string? _ownerOfFound;
        private PrivacyOption _privacy;

        private DateTime? _periodStart;
        private DateTime? _periodEnd;
        private bool _allowMassDelete;
        private string? _validationError;
        private CopyPreview? _preview;
        private CopyRunner? _runner;
        private IReadOnlyList<CopyRow> _rows = Array.Empty<CopyRow>();
        private bool _isBusy;
        private bool _isWriting;
        private bool _isStopping;
        private string _progressText = string.Empty;
        private int _progressValue;
        private int _progressMaximum;
        private string? _resultTitle;
        private string? _resultText;
        private bool _resultIsSuccess;
        private UserMessage? _error;
        private CancellationTokenSource? _cancellation;

        /// <param name="settings">Настройки копирования между запусками; <c>null</c> — не запоминаются.</param>
        /// <param name="timer">Таймер автокопирования; <c>null</c> — автокопирование не срабатывает (тесты экрана).</param>
        /// <param name="autostart">Автозапуск с Windows; <c>null</c> — недоступен (демонстрационный режим).</param>
        public CopyViewModel(
            IDialogService dialogs,
            IOutlookCalendars? outlook,
            Func<DateTime>? today = null,
            ICopySettingsStore? settings = null,
            IOneShotTimer? timer = null,
            IAutostart? autostart = null,
            Func<bool>? otherBusy = null)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _outlook = outlook;
            _today = today ?? (() => DateTime.Today);
            _settingsStore = settings;
            _otherBusy = otherBusy ?? (() => false);

            // Сохранённое: источник, календарь Outlook, правило личных, период (скользящий — дни назад и вперёд от сегодня).
            // По умолчанию — от месяца назад до трёх месяцев вперёд: недавнее и ближайшее, что видно в календаре.
            CopySettings saved = settings?.Load() ?? new CopySettings();
            _sourceKind = OutlookAvailable && saved.FromOutlook ? SourceKind.Outlook : SourceKind.Ews;
            _savedCalendar = saved.OutlookCalendar;
            _savedCalendarKey = saved.OutlookCalendarKey;
            _savedOwner = saved.OutlookOwner;
            _daysBack = saved.DaysBack;
            _daysForward = saved.DaysForward;
            (DateTime first, DateTime last) = saved.Window(_today());
            _periodStart = first;
            _periodEnd = last;
            PrivacyOptions = new[]
            {
                new PrivacyOption(PrivateEvents.HideDetails, "только «занято»"),
                new PrivacyOption(PrivateEvents.CopyAll, "копировать полностью"),
                new PrivacyOption(PrivateEvents.Skip, "не копировать"),
            };
            _privacy = PrivacyOptions.First(o => o.Value == saved.PrivateEvents);

            RefreshOutlookCommand = new AsyncCommand(() => RefreshOutlookAsync(substitute: true), () => OutlookAvailable && !_isBusy && !_isLoadingOutlook);
            FindOutlookCalendarCommand = new AsyncCommand(
                FindOutlookCalendarAsync, () => OutlookAvailable && !_isBusy && !_isLoadingOutlook && _calendarSearch.Trim().Length > 0);
            PreviewCommand = new AsyncCommand(PreviewAsync, () => CanPreview);
            CopyCommand = new AsyncCommand(CopyAsync, () => CanCopy);
            StopCommand = new RelayCommand(Stop, () => _isBusy && !_isStopping);
            SelectAllCommand = new RelayCommand(() => Select(true), () => HasPreview && !_isBusy);
            SelectNoneCommand = new RelayCommand(() => Select(false), () => HasPreview && !_isBusy);
            SetRecentCommand = new RelayCommand(() => SetPeriod(_today().AddMonths(-1), _today().AddMonths(3)), () => !_isBusy);
            SetThisYearCommand = new RelayCommand(() => SetPeriod(new DateTime(_today().Year, 1, 1), new DateTime(_today().Year, 12, 31)), () => !_isBusy);
            Auto = new AutoCopyViewModel(timer ?? new InertTimer(), autostart, RunScheduledAsync, SaveSettings, saved.AutoCopy, saved.IntervalMinutes);
        }

        /// <summary>Автокопирование «раз в N» — расписание; запуск — <see cref="RunScheduledAsync"/>.</summary>
        public AutoCopyViewModel Auto { get; }

        /// <summary>Варианты для личных (Private) событий источника.</summary>
        public IReadOnlyList<PrivacyOption> PrivacyOptions { get; }

        public PrivacyOption Privacy
        {
            get => _privacy;
            set
            {
                if (value != null && !_isBusy && SetProperty(ref _privacy, value))
                {
                    Invalidate();
                    SaveSettings();
                }
            }
        }

        /// <summary>
        /// Один автоматический запуск: за скользящий период (те же дни назад и вперёд, что выбраны сейчас, — от текущей
        /// даты) выполняются рекомендованные действия плана (<see cref="CopyPreview.Recommended"/>) — без вопросов.
        /// Открытый предпросмотр закрывается: после записи он устарел бы, и его выполнение создало бы дубли.
        /// Идёт другая операция — пропуск (расписание повторит через минуту).
        /// </summary>
        public async Task<AutoRunOutcome> RunScheduledAsync()
        {
            // Одна операция за раз на всё окно: идёт копирование, очистка или открыт вопрос («Скопировать?», «Подключение»,
            // подтверждение удаления) — ждать; иначе календарь изменился бы под открытым вопросом.
            if (_isBusy || _isLoadingOutlook || _otherBusy() || _dialogs.IsShowingDialog)
            {
                return new AutoRunOutcome("пропущено: идёт другая операция", isProblem: false, skipped: true);
            }

            ITargetSession? target = _target;
            if (target is null)
            {
                return new AutoRunOutcome("приёмник не настроен", isProblem: true);
            }

            if (_sourceKind == SourceKind.Outlook)
            {
                // Список — заново перед каждым запуском: Outlook мог открыться позже программы (запуск с Windows) или
                // быть перезапущен. Выбранный календарь не подменяется другим: не нашёлся — запуск не выполняется.
                await RefreshOutlookAsync(substitute: false).ConfigureAwait(true);
                if (_outlookCalendar is null)
                {
                    string name = _savedCalendar ?? _savedOwner ?? "выбранный";
                    return new AutoRunOutcome(
                        OutlookError ?? ("календарь «" + name + "» не найден в Outlook — откройте его в Outlook"), isProblem: true);
                }
            }

            ISourceCalendar? source = SelectedSource;
            if (source is null)
            {
                return new AutoRunOutcome("источник по EWS не настроен", isProblem: true);
            }

            string? sameMailbox = SameMailboxError();
            if (sameMailbox != null)
            {
                return new AutoRunOutcome(sameMailbox, isProblem: true, needsAttention: true);
            }

            // Скользящий период: те же дни назад и вперёд — от сегодняшней даты; поля периода на экране сдвигаются тоже.
            CopySettings settings = CurrentSettings();
            (DateTime first, DateTime last) = settings.Window(_today());
            ShowPeriod(first, last);
            (DateTime startUtc, DateTime endUtc) = target.TimeZone.PeriodFromLocalDates(first, last);
            ClearPreview();
            ClearResult();
            Begin("Автокопирование: читаю календари…");
            Task<AutoRunOutcome> work = AutoCoreAsync(target, source, settings, startUtc, endUtc);
            Operation = work;
            return await work.ConfigureAwait(true);
        }

        private async Task<AutoRunOutcome> AutoCoreAsync(ITargetSession target, ISourceCalendar source, CopySettings settings, DateTime startUtc, DateTime endUtc)
        {
            string stamp = DateTime.Now.ToString("HH:mm", RussianText.Culture);
            try
            {
                var runner = new CopyRunner(source, target.CopyTarget, target.StopsBatch, settings.PrivateEvents);
                (CopyPreview _, CopyResult result) = await runner.SyncAsync(startUtc, endUtc, NewProgress(), _cancellation!.Token).ConfigureAwait(true);
                ShowResult(result, target);
                ResultTitle = "Автокопирование в " + stamp + ": " + (ResultTitle ?? string.Empty).ToLower(RussianText.Culture);
                if (result.RefusedReason != null)
                {
                    return new AutoRunOutcome("не выполнено — " + result.RefusedReason + " Откройте программу и проверьте план вручную.", isProblem: true);
                }

                if (result.Fatal != null)
                {
                    return new AutoRunOutcome(target.Describe(result.Fatal).Title, isProblem: true, needsAttention: target.NeedsAttention(result.Fatal));
                }

                string done = result.Done == 0 ? "изменений нет" : DescribeDone(result).TrimEnd('.').ToLower(RussianText.Culture);
                return result.Failures.Count == 0
                    ? new AutoRunOutcome(done, isProblem: false)
                    : new AutoRunOutcome(done + ", не удалось: " + RussianText.Number(result.Failures.Count), isProblem: true);
            }
            catch (OperationCanceledException)
            {
                return new AutoRunOutcome("остановлено", isProblem: false);
            }
#pragma warning disable CA1031 // Ошибка показывается и попадает в уведомление; расписание продолжается.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Error = DescribeError(ex, target);
                return new AutoRunOutcome(Error.Title, isProblem: true, needsAttention: target.NeedsAttention(ex));
            }
            finally
            {
                End();
            }
        }

        /// <summary>Текущие настройки копирования (для сохранения и автокопирования).</summary>
        /// <remarks>
        /// Период хранится как дни назад и вперёд от даты, когда человек его выбрал (<see cref="_daysBack"/>), — а не
        /// пересчитывается из полей дат: иначе «скользящий» период стоял бы на датах дня запуска программы.
        /// </remarks>
        internal CopySettings CurrentSettings()
        {
            return new CopySettings(
                _sourceKind == SourceKind.Outlook,
                _outlookCalendar?.Label ?? _savedCalendar,
                _privacy.Value,
                Auto?.IsEnabled ?? false,
                Auto?.IntervalMinutes ?? 30,
                _daysBack,
                _daysForward,
                _foundShared.Any(f => f.Key == _outlookCalendar?.Key) ? _ownerOfFound : _savedOwner,
                _outlookCalendar?.Key ?? _savedCalendarKey);
        }

        /// <summary>Источник и приёмник — один ящик: копия в самого себя (только по EWS; в демонстрации ящик один).</summary>
        private string? SameMailboxError() =>
            _sourceKind == SourceKind.Ews && _ewsSource != null && _target != null && !_target.IsDemo
                && string.Equals(_ewsSource.TargetLabel, _target.TargetLabel, StringComparison.OrdinalIgnoreCase)
                ? "источник по EWS и приёмник — один и тот же ящик"
                : null;

        /// <summary>Показать период на экране, не меняя запомненных дней (автокопирование сдвигает его каждый день).</summary>
        private void ShowPeriod(DateTime first, DateTime last)
        {
            _periodStart = first;
            _periodEnd = last;
            OnPropertyChanged(nameof(PeriodStart));
            OnPropertyChanged(nameof(PeriodEnd));
        }

        private void SaveSettings()
        {
            // Пока модель строится (Auto ещё нет), сохранять нечего: значения только что прочитаны.
            if (_settingsStore is null || Auto is null)
            {
                return;
            }

            try
            {
                _settingsStore.Save(CurrentSettings());
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                // Папка программы только для чтения — настройки не запомнятся, работа продолжается.
                System.Diagnostics.Trace.TraceWarning("Настройки копирования не сохранены: " + ex.Message);
            }
        }

        /// <summary>Таймер, который никогда не срабатывает (модель без расписания — тесты экрана).</summary>
        private sealed class InertTimer : IOneShotTimer
        {
            public event EventHandler? Elapsed
            {
                add { }
                remove { }
            }

            public void Schedule(TimeSpan due)
            {
            }

            public void Cancel()
            {
            }

            public void Dispose()
            {
            }
        }

        public AsyncCommand RefreshOutlookCommand { get; }

        /// <summary>Найти календарь Outlook по <see cref="CalendarSearch"/>.</summary>
        public AsyncCommand FindOutlookCalendarCommand { get; }

        public AsyncCommand PreviewCommand { get; }

        public AsyncCommand CopyCommand { get; }

        public RelayCommand StopCommand { get; }

        public RelayCommand SelectAllCommand { get; }

        public RelayCommand SelectNoneCommand { get; }

        public RelayCommand SetRecentCommand { get; }

        public RelayCommand SetThisYearCommand { get; }

        // ---- Приёмник и источник ----

        public bool HasTarget => _target != null;

        public string TargetLabel => _target?.TargetLabel ?? "Приёмник не настроен — кнопка «Приёмник…» вверху окна.";

        public string TimeZoneText => _target is null ? string.Empty : RussianText.Zone(_target.TimeZone);

        /// <summary>Новый приёмник (или его нет): предпросмотр прежнего сбрасывается. Во время операции не меняется.</summary>
        public void SetTarget(ITargetSession? target)
        {
            ThrowIfBusy();
            _target = target;
            if (target != null)
            {
                Auto?.Resume(); // подключение исправили — приостановленное автокопирование продолжается
            }

            Invalidate();
            OnPropertyChanged(nameof(HasTarget));
            OnPropertyChanged(nameof(TargetLabel));
            OnPropertyChanged(nameof(TimeZoneText));
            Refresh();
        }

        /// <summary>Подключение «Источник по EWS» (или его нет).</summary>
        public void SetEwsSource(ITargetSession? source)
        {
            ThrowIfBusy();
            _ewsSource = source;
            if (source != null)
            {
                Auto?.Resume();
            }
            if (_sourceKind == SourceKind.Ews)
            {
                Invalidate();
            }

            OnPropertyChanged(nameof(HasEwsSource));
            OnPropertyChanged(nameof(EwsSourceLabel));
            Refresh();
        }

        public bool OutlookAvailable => _outlook?.IsAvailable ?? false;

        public bool UseOutlook
        {
            get => _sourceKind == SourceKind.Outlook;
            set
            {
                if (value)
                {
                    SetSourceKind(SourceKind.Outlook);
                }
            }
        }

        public bool UseEws
        {
            get => _sourceKind == SourceKind.Ews;
            set
            {
                if (value)
                {
                    SetSourceKind(SourceKind.Ews);
                }
            }
        }

        public IReadOnlyList<SourceChoice> OutlookCalendars
        {
            get => _outlookCalendars;
            private set => SetProperty(ref _outlookCalendars, value);
        }

        public SourceChoice? OutlookCalendar
        {
            get => _outlookCalendar;
            set
            {
                if (SetProperty(ref _outlookCalendar, value))
                {
                    Invalidate();
                    Refresh();
                    // Пустой список (Outlook закрыт) выбор не стирает: календарь найдётся при следующем «Обновить».
                    // Тот же календарь после перечитывания списка — не новый выбор: файл не перезаписывается.
                    if (value != null && value.Key != _savedCalendarKey)
                    {
                        _savedCalendar = value.Label;
                        _savedCalendarKey = value.Key;
                        SaveSettings();
                    }
                }
            }
        }

        /// <summary>Что искать: ФИО владельца общего календаря или часть названия календаря Outlook.</summary>
        public string CalendarSearch
        {
            get => _calendarSearch;
            set
            {
                if (SetProperty(ref _calendarSearch, value ?? string.Empty))
                {
                    FindOutlookCalendarCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Почему список Outlook пуст (Outlook не открыт и т. п.); <c>null</c> — всё в порядке.</summary>
        public string? OutlookError
        {
            get => _outlookError;
            private set => SetProperty(ref _outlookError, value);
        }

        public bool HasEwsSource => _ewsSource != null;

        public string EwsSourceLabel => _ewsSource?.TargetLabel ?? "Не настроен — кнопка «Источник (EWS)…» вверху окна.";

        // ---- Период ----

        public DateTime? PeriodStart
        {
            get => _periodStart;
            set
            {
                if (SetProperty(ref _periodStart, value?.Date))
                {
                    if (value.HasValue)
                    {
                        _daysBack = Days(value.Value.Date, _today().Date);
                    }

                    Invalidate();
                    SaveSettings();
                }
            }
        }

        public DateTime? PeriodEnd
        {
            get => _periodEnd;
            set
            {
                if (SetProperty(ref _periodEnd, value?.Date))
                {
                    if (value.HasValue)
                    {
                        _daysForward = Days(_today().Date, value.Value.Date);
                    }

                    Invalidate();
                    SaveSettings();
                }
            }
        }

        /// <summary>Дней от <paramref name="from"/> до <paramref name="to"/>, не меньше нуля и не больше предела настроек.</summary>
        private static int Days(DateTime from, DateTime to) => (int)Math.Max(0, Math.Min(CopySettings.MaxDays, (to - from).TotalDays));

        public string? ValidationError
        {
            get => _validationError;
            private set => SetProperty(ref _validationError, value);
        }

        // ---- Предпросмотр ----

        public bool HasPreview => _preview != null;

        public IReadOnlyList<CopyRow> Rows
        {
            get => _rows;
            private set
            {
                if (SetProperty(ref _rows, value))
                {
                    OnPropertyChanged(nameof(RowCount));
                }
            }
        }

        public int RowCount => _rows.Count;

        public int UnchangedCount => _preview?.Plan.UnchangedCount ?? 0;

        /// <summary>Одной строкой: сколько создастся, перезапишется, удалится и сколько уже совпадает.</summary>
        public string Summary => _preview is null ? string.Empty
            : Describe(_preview.Plan) + (_preview.MatchingOwn.Count == 0 ? string.Empty
                : ", уже есть у вас " + RussianText.Number(_preview.MatchingOwn.Count) + " (не отмечены)");

        public bool NothingToDo => _preview != null && _preview.Plan.IsEmpty;

        /// <summary>Подозрение на массовое удаление — почему план не выполняется без подтверждения.</summary>
        public string? BlockReason => _preview?.Plan.BlockReason;

        public bool IsBlocked => BlockReason != null;

        /// <summary>Источник прочитан не полностью — удаления в план не включены.</summary>
        public string? SuppressedReason => _preview?.Plan.DeletesSuppressedReason;

        /// <summary>Замечания чтения источника (что не попадёт в копию и почему); <c>null</c> — замечаний нет.</summary>
        public string? WarningsText => _preview is null || _preview.Warnings.Count == 0
            ? null
            : string.Join("\n", _preview.Warnings.Take(10).Select(w => "• " + w))
              + (_preview.Warnings.Count > 10 ? "\n• …и ещё " + RussianText.Number(_preview.Warnings.Count - 10) : string.Empty);

        public string? ConflictText => _preview is null || _preview.Plan.ConflictingSourceIds.Count == 0
            ? null
            : "Событий источника с одинаковым идентификатором: " + RussianText.Number(_preview.Plan.ConflictingSourceIds.Count)
              + " — они пропущены: неясно, какое из них копировать.";

        /// <summary>Пользователь подтвердил массовое удаление копий (действует со следующего предпросмотра).</summary>
        public bool AllowMassDelete
        {
            get => _allowMassDelete;
            set
            {
                if (SetProperty(ref _allowMassDelete, value))
                {
                    Invalidate();
                }
            }
        }

        // ---- Ход работы и итог ----

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                    Refresh();
                }
            }
        }

        public bool IsIdle => !_isBusy;

        public string ProgressText
        {
            get => _progressText;
            private set => SetProperty(ref _progressText, value);
        }

        public int ProgressValue
        {
            get => _progressValue;
            private set => SetProperty(ref _progressValue, value);
        }

        public int ProgressMaximum
        {
            get => _progressMaximum;
            private set
            {
                if (SetProperty(ref _progressMaximum, value))
                {
                    OnPropertyChanged(nameof(IsProgressIndeterminate));
                }
            }
        }

        public bool IsProgressIndeterminate => _progressMaximum == 0;

        public string? ResultTitle
        {
            get => _resultTitle;
            private set
            {
                if (SetProperty(ref _resultTitle, value))
                {
                    OnPropertyChanged(nameof(HasResult));
                }
            }
        }

        public string? ResultText
        {
            get => _resultText;
            private set => SetProperty(ref _resultText, value);
        }

        public bool ResultIsSuccess
        {
            get => _resultIsSuccess;
            private set => SetProperty(ref _resultIsSuccess, value);
        }

        public bool HasResult => _resultTitle != null;

        public UserMessage? Error
        {
            get => _error;
            private set
            {
                if (SetProperty(ref _error, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => _error != null;

        /// <summary>Текущая операция — окно ждёт её при закрытии после «Остановить».</summary>
        public Task Operation { get; private set; } = Task.CompletedTask;

        public void Stop()
        {
            if (!_isBusy || _isStopping)
            {
                return;
            }

            _isStopping = true;
            ProgressText = _isWriting ? "Останавливаю после текущего события…" : "Останавливаю…";
            _cancellation?.Cancel();
            StopCommand.RaiseCanExecuteChanged();
        }

        private ISourceCalendar? SelectedSource => _sourceKind == SourceKind.Outlook ? _outlookCalendar?.Calendar : _ewsSource?.Source;

        private bool CanPreview => _target != null && SelectedSource != null && !_isBusy;

        private bool CanCopy => _preview != null && _runner != null && _preview.Plan.CanApply && !_preview.Plan.IsEmpty && !_isBusy;

        private void SetSourceKind(SourceKind kind)
        {
            if (_sourceKind == kind || _isBusy)
            {
                return;
            }

            _sourceKind = kind;
            OnPropertyChanged(nameof(UseOutlook));
            OnPropertyChanged(nameof(UseEws));
            Invalidate();
            Refresh();
            SaveSettings();
        }

        /// <param name="substitute">Прежний календарь не нашёлся — выбрать другой (по просьбе человека) или оставить пусто.</param>
        private async Task RefreshOutlookAsync(bool substitute)
        {
            if (_outlook is null)
            {
                return;
            }

            _isLoadingOutlook = true;
            Refresh();
            try
            {
                OutlookError = null;
                // Найденные по имени общие календари остаются в списке и после «Обновить».
                IReadOnlyList<SourceChoice> listed = await _outlook.ListAsync().ConfigureAwait(true);
                SourceChoice[] calendars = listed.Concat(_foundShared.Where(f => listed.All(c => c.Key != f.Key))).ToArray();
                OutlookCalendars = calendars;

                // Выбор: прежний — по идентификатору папки, затем по подписи (запомнен между запусками); иначе общий
                // календарь, найденный когда-то по имени владельца, — заново через адресную книгу.
                SourceChoice? chosen = calendars.FirstOrDefault(c => _savedCalendarKey != null && c.Key == _savedCalendarKey)
                    ?? calendars.FirstOrDefault(c => c.Label == _savedCalendar);
                if (chosen is null && _savedOwner != null && _foundShared.Count == 0)
                {
                    chosen = await TryFindSharedAsync(_savedOwner).ConfigureAwait(true);
                    if (chosen != null)
                    {
                        calendars = calendars.Concat(new[] { chosen }).ToArray();
                        OutlookCalendars = calendars;
                    }
                }

                // Прежнего нет: человеку (кнопка «Обновить», старт) — открытый сейчас в Outlook, иначе первый. Расписанию —
                // ничего: молча копировать другой календарь в чужой ящик нельзя.
                bool firstChoice = _savedCalendarKey is null && _savedCalendar is null && _savedOwner is null;
                OutlookCalendar = chosen
                    ?? (substitute || firstChoice ? calendars.FirstOrDefault(c => c.IsCurrent) ?? calendars.FirstOrDefault() : null);
                if (calendars.Length == 0)
                {
                    OutlookError = "В Outlook не найдено ни одного календаря.";
                }
            }
#pragma warning disable CA1031 // Outlook не открыт или занят — показать текст, окно рабочее.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OutlookCalendars = Array.Empty<SourceChoice>();
                OutlookCalendar = null;
                OutlookError = ex.Message;
            }
            finally
            {
                _isLoadingOutlook = false;
                Refresh();
            }
        }

        /// <summary>
        /// Найти календарь по введённому тексту: сначала среди календарей Outlook (часть названия, без учёта регистра и
        /// «ё»), затем — общий календарь владельца через адресную книгу Outlook. Найденный выбирается.
        /// </summary>
        private async Task FindOutlookCalendarAsync()
        {
            string text = (_calendarSearch ?? string.Empty).Trim();
            OutlookError = null;
            if (text.Length == 0)
            {
                OutlookError = "Введите ФИО владельца или часть названия календаря.";
                return;
            }

            SetSourceKind(SourceKind.Outlook);
            SourceChoice? local = _outlookCalendars.FirstOrDefault(c => Fold(c.Label).Contains(Fold(text)));
            if (local != null)
            {
                OutlookCalendar = local;
                return;
            }

            _isLoadingOutlook = true;
            Refresh();
            try
            {
                SourceChoice found = await _outlook!.FindSharedAsync(text).ConfigureAwait(true);
                _foundShared.RemoveAll(f => f.Key == found.Key);
                _foundShared.Add(found);
                _ownerOfFound = text;
                _savedOwner = text;
                OutlookCalendars = _outlookCalendars.Where(c => c.Key != found.Key).Concat(new[] { found }).ToArray();
                OutlookCalendar = found;
            }
#pragma warning disable CA1031 // Не найден, нет прав, Outlook закрыт — текст под полем, окно рабочее.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OutlookError = ex.Message;
            }
            finally
            {
                _isLoadingOutlook = false;
                Refresh();
            }
        }

        /// <summary>Общий календарь по запомненному имени владельца; не нашёлся — <c>null</c> (выбор по умолчанию).</summary>
        private async Task<SourceChoice?> TryFindSharedAsync(string owner)
        {
            try
            {
                SourceChoice found = await _outlook!.FindSharedAsync(owner).ConfigureAwait(true);
                _foundShared.Add(found);
                _ownerOfFound = owner;
                return found;
            }
#pragma warning disable CA1031 // Владельца больше нет или нет доступа — выбирается календарь по умолчанию.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }

        private static string Fold(string text) => text.ToLower(RussianText.Culture).Replace('ё', 'е');

        private Task PreviewAsync()
        {
            Operation = PreviewCoreAsync();
            return Operation;
        }

        private async Task PreviewCoreAsync()
        {
            ITargetSession target = _target!;
            ISourceCalendar source = SelectedSource!;
            if (!TryPeriod(target.TimeZone, out DateTime startUtc, out DateTime endUtc))
            {
                return;
            }

            string? sameMailbox = SameMailboxError();
            if (sameMailbox != null)
            {
                ValidationError = char.ToUpper(sameMailbox[0], RussianText.Culture) + sameMailbox.Substring(1) + " — выберите другой источник.";
                return;
            }

            ClearPreview();
            ClearResult();
            Begin("Читаю календарь-приёмник…");
            var runner = new CopyRunner(source, target.CopyTarget, target.StopsBatch, _privacy.Value);
            try
            {
                CopyPreview preview = await runner
                    .PreviewAsync(startUtc, endUtc, _allowMassDelete, NewProgress(), _cancellation!.Token)
                    .ConfigureAwait(true);
                _cancellation.Token.ThrowIfCancellationRequested();
                _preview = preview;
                _runner = runner;
                Rows = BuildRows(preview, target.TimeZone);
                RaisePreview();
            }
            catch (OperationCanceledException)
            {
                // Предпросмотр не открывается.
            }
#pragma warning disable CA1031 // Ошибка чтения показывается; при чтении ничего не меняется.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Error = DescribeError(ex, target);
            }
            finally
            {
                End();
            }
        }

        private Task CopyAsync()
        {
            Operation = CopyCoreAsync();
            return Operation;
        }

        private async Task CopyCoreAsync()
        {
            CopyPreview preview = _preview!;
            CopyRunner runner = _runner!;
            ITargetSession target = _target!;
            SyncAction[] selected = _rows.Where(r => r.IsSelected).Select(r => r.Action).ToArray();
            if (selected.Length == 0)
            {
                ValidationError = "Не выбрано ни одного действия — отметьте нужные строки.";
                return;
            }

            if (!_dialogs.Ask("Скопировать?", ConfirmationText(selected, runner.SourceLabel, target.TargetLabel), "Скопировать"))
            {
                return;
            }

            // Пока окно было открыто, предпросмотр мог устареть (сменили приёмник, источник): выполнять только свой.
            if (!ReferenceEquals(_preview, preview) || _isBusy)
            {
                ValidationError = "Данные изменились, пока открыт был вопрос, — постройте предпросмотр заново.";
                return;
            }

            ClearResult();
            _isWriting = true;
            Begin("Записываю в приёмник…");
            try
            {
                CopyResult result = await runner.RunAsync(preview, selected, NewProgress(), _cancellation!.Token).ConfigureAwait(true);
                ShowResult(result, target);
            }
#pragma warning disable CA1031 // Непредвиденная ошибка показывается; уже записанное останется в итоге следующего предпросмотра.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ResultIsSuccess = false;
                ResultTitle = "Копирование прервано ошибкой";
                ResultText = "Постройте предпросмотр заново — уже записанное будет учтено.";
                Error = target.Describe(ex);
            }
            finally
            {
                // Предпросмотр израсходован: следующий запуск — по новому.
                _isWriting = false;
                ClearPreview();
                End();
            }
        }

        private void ShowResult(CopyResult result, ITargetSession target)
        {
            string done = DescribeDone(result);
            var text = new StringBuilder(done);
            if (result.RefusedReason != null)
            {
                ResultIsSuccess = false;
                ResultTitle = "Копирование не выполнялось";
                ResultText = result.RefusedReason;
                return;
            }

            if (result.Failures.Count > 0)
            {
                text.Append("\n\nНе удалось (").Append(RussianText.Number(result.Failures.Count)).Append("):");
                foreach (CopyFailure failure in result.Failures.Take(20))
                {
                    string subject = failure.Action.Source?.Subject ?? failure.Action.Copy?.Subject ?? "(без темы)";
                    text.Append("\n• ").Append(subject).Append(" — ").Append(target.Describe(failure.Exception).Title);
                }

                if (result.Failures.Count > 20)
                {
                    text.Append("\n• …и ещё ").Append(RussianText.Number(result.Failures.Count - 20));
                }
            }

            if (result.Fatal != null)
            {
                ResultIsSuccess = false;
                ResultTitle = "Копирование остановлено ошибкой подключения";
                Error = target.Describe(result.Fatal);
            }
            else if (result.Cancelled)
            {
                ResultIsSuccess = false;
                ResultTitle = "Остановлено";
                text.Append("\nОстальное не тронуто. Чтобы продолжить, постройте предпросмотр заново.");
            }
            else
            {
                ResultIsSuccess = result.Failures.Count == 0;
                ResultTitle = result.Failures.Count == 0 ? "Готово" : "Готово, но не всё";
            }

            ResultText = text.ToString();
        }

        private static string DescribeDone(CopyResult result)
        {
            if (result.Done == 0)
            {
                return "Ничего не записано.";
            }

            var parts = new List<string>();
            if (result.Created > 0)
            {
                parts.Add("создано " + RussianText.Count(result.Created, "событие", "события", "событий"));
            }

            if (result.Updated > 0)
            {
                parts.Add("перезаписано " + RussianText.Number(result.Updated));
            }

            if (result.Deleted > 0)
            {
                parts.Add("удалено копий " + RussianText.Number(result.Deleted));
            }

            string line = string.Join(", ", parts) + ".";
            return char.ToUpper(line[0], RussianText.Culture) + line.Substring(1);
        }

        internal static string Describe(SyncPlan plan)
        {
            if (plan.IsEmpty)
            {
                return plan.UnchangedCount == 0
                    ? "Копировать нечего"
                    : "Всё уже скопировано: " + RussianText.Count(plan.UnchangedCount, "событие совпадает", "события совпадают", "событий совпадают");
            }

            var parts = new List<string>();
            int create = plan.Count(SyncActionKind.Create);
            int update = plan.Count(SyncActionKind.Update) + plan.Count(SyncActionKind.Recreate);
            int delete = plan.Count(SyncActionKind.Delete);
            if (create > 0)
            {
                parts.Add("создать " + RussianText.Number(create));
            }

            if (update > 0)
            {
                parts.Add("перезаписать " + RussianText.Number(update));
            }

            if (delete > 0)
            {
                parts.Add("удалить копий " + RussianText.Number(delete));
            }

            if (plan.UnchangedCount > 0)
            {
                parts.Add("без изменений " + RussianText.Number(plan.UnchangedCount));
            }

            return string.Join(", ", parts);
        }

        private static string ConfirmationText(IReadOnlyCollection<SyncAction> selected, string source, string target)
        {
            int create = selected.Count(a => a.Kind == SyncActionKind.Create);
            int update = selected.Count(a => a.Kind == SyncActionKind.Update || a.Kind == SyncActionKind.Recreate);
            int delete = selected.Count(a => a.Kind == SyncActionKind.Delete);
            var text = new StringBuilder();
            text.Append("Откуда: ").Append(source).Append("\nКуда: ").Append(target).Append("\n");

            // Только то, что будет сделано: строки с нулём не показываются.
            if (create > 0)
            {
                text.Append("\n• создать ").Append(RussianText.Count(create, "событие", "события", "событий"));
            }

            if (update > 0)
            {
                text.Append("\n• перезаписать ").Append(RussianText.Count(update, "копию", "копии", "копий"));
            }

            if (delete > 0)
            {
                text.Append("\n• удалить ").Append(RussianText.Count(delete, "копию", "копии", "копий"))
                    .Append(" — их событий больше нет в источнике (в папку «Удалённые», можно вернуть)");
            }

            text.Append("\n\nИсточник не меняется, ваши собственные события не трогаются, участникам ничего не рассылается.");
            return text.ToString();
        }

        private static IReadOnlyList<CopyRow> BuildRows(CopyPreview preview, TargetTimeZone zone)
        {
            return preview.Plan.Actions.Select(action =>
            {
                SourceItem? item = action.Source != null && preview.SourceItems.TryGetValue(action.SourceId, out SourceItem? found) ? found : null;
                string subject = item?.Subject ?? action.Copy?.Subject ?? "(без темы)";
                DateTime start = item?.StartUtc ?? action.Copy!.SpanStartUtc;
                string when = RussianText.Moment(start, zone) + (item?.Recurrence != null || action.Copy?.Kind == Core.Model.ItemKind.RecurringMaster ? " (серия)" : string.Empty);
                // Такое же событие человек уже завёл сам — копия по умолчанию не создаётся (иначе дубль), но её можно отметить.
                bool duplicate = preview.IsDuplicateOfOwn(action);
                return new CopyRow(
                    action,
                    KindLabel(action.Kind),
                    string.IsNullOrEmpty(subject) ? "(без темы)" : subject,
                    when,
                    duplicate ? "У вас уже есть такое же событие" : ReasonLabel(action.Reason))
                {
                    IsSelected = !duplicate,
                };
            }).ToArray();
        }

        internal static string KindLabel(SyncActionKind kind)
        {
            switch (kind)
            {
                case SyncActionKind.Create:
                    return "Создать";
                case SyncActionKind.Update:
                    return "Перезаписать";
                case SyncActionKind.Recreate:
                    return "Пересоздать";
                default:
                    return "Удалить копию";
            }
        }

        internal static string ReasonLabel(SyncReason reason)
        {
            switch (reason)
            {
                case SyncReason.NewInSource:
                    return "Новое в источнике";
                case SyncReason.ChangedInSource:
                    return "Изменилось в источнике";
                case SyncReason.UnknownCopyVersion:
                    return "Копия старой версии программы";
                case SyncReason.KindChanged:
                    return "Стало серией или перестало ею быть";
                case SyncReason.DeletedInSource:
                    return "Удалено в источнике";
                default:
                    return "Лишняя копия того же события";
            }
        }

        private UserMessage DescribeError(Exception ex, ITargetSession target)
        {
            return _sourceKind == SourceKind.Outlook && !(ex is OperationCanceledException) && target.Describe(ex).Title == ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown).Title
                ? new UserMessage("Не удалось прочитать календарь Outlook или приёмник", "Проверьте, что Outlook открыт и не показывает окно, затем повторите.", ex.Message)
                : target.Describe(ex);
        }

        private bool TryPeriod(TargetTimeZone zone, out DateTime startUtc, out DateTime endUtc)
        {
            startUtc = endUtc = default;
            ValidationError = null;
            if (_periodStart is null || _periodEnd is null)
            {
                ValidationError = "Укажите первый и последний день периода.";
                return false;
            }

            if (_periodEnd < _periodStart)
            {
                ValidationError = "Последний день периода раньше первого.";
                return false;
            }

            if (_periodStart < TargetTimeZone.MinPeriodDate || _periodEnd > TargetTimeZone.MaxPeriodDate)
            {
                ValidationError = "Даты периода должны быть в пределах 1601–4500 годов.";
                return false;
            }

            (startUtc, endUtc) = zone.PeriodFromLocalDates(_periodStart.Value, _periodEnd.Value);
            return true;
        }

        private void SetPeriod(DateTime start, DateTime end)
        {
            PeriodStart = start;
            PeriodEnd = end;
        }

        private void Select(bool selected)
        {
            foreach (CopyRow row in _rows)
            {
                row.IsSelected = selected;
            }
        }

        private IProgress<CopyProgress> NewProgress()
        {
            return new Progress<CopyProgress>(p =>
            {
                ProgressMaximum = p.Total;
                ProgressValue = Math.Min(p.Completed, p.Total);
                if (_isStopping)
                {
                    return;
                }

                switch (p.Stage)
                {
                    case CopyStage.ReadingTarget:
                        ProgressText = "Читаю календарь-приёмник…";
                        break;
                    case CopyStage.ReadingSource:
                        ProgressText = p.Completed > 0 ? "Читаю источник: " + RussianText.Number(p.Completed) : "Читаю источник…";
                        break;
                    default:
                        ProgressText = "Записываю: " + RussianText.Number(p.Completed) + " из " + RussianText.Number(p.Total);
                        break;
                }
            });
        }

        private void Begin(string text)
        {
            Error = null;
            ValidationError = null;
            _isStopping = false;
            ProgressText = text;
            ProgressValue = 0;
            ProgressMaximum = 0;
            _cancellation = new CancellationTokenSource();
            IsBusy = true;
        }

        private void End()
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _isStopping = false;
            ProgressText = string.Empty;
            IsBusy = false;
        }

        private void Invalidate()
        {
            ValidationError = null;
            if (_preview != null)
            {
                ClearPreview();
            }
        }

        private void ClearPreview()
        {
            _preview = null;
            _runner = null;
            Rows = Array.Empty<CopyRow>();
            RaisePreview();
        }

        private void ClearResult()
        {
            ResultTitle = null;
            ResultText = null;
            ResultIsSuccess = false;
            Error = null;
        }

        private void RaisePreview()
        {
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(NothingToDo));
            OnPropertyChanged(nameof(BlockReason));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(SuppressedReason));
            OnPropertyChanged(nameof(ConflictText));
            OnPropertyChanged(nameof(WarningsText));
            OnPropertyChanged(nameof(UnchangedCount));
            Refresh();
        }

        private void Refresh()
        {
            RefreshOutlookCommand.RaiseCanExecuteChanged();
            FindOutlookCalendarCommand.RaiseCanExecuteChanged();
            PreviewCommand.RaiseCanExecuteChanged();
            CopyCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            SelectAllCommand.RaiseCanExecuteChanged();
            SelectNoneCommand.RaiseCanExecuteChanged();
            SetRecentCommand.RaiseCanExecuteChanged();
            SetThisYearCommand.RaiseCanExecuteChanged();
        }

        private void ThrowIfBusy()
        {
            if (_isBusy)
            {
                throw new InvalidOperationException("Нельзя сменить подключение во время операции.");
            }
        }
    }
}
