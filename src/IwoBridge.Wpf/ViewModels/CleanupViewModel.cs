using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Шаг экрана очистки.</summary>
    public enum CleanupStep
    {
        /// <summary>Выбор объёма; предпросмотра нет или он устарел.</summary>
        Choose,

        /// <summary>Идёт чтение календаря для предпросмотра.</summary>
        Previewing,

        /// <summary>Предпросмотр готов: видно, что будет удалено и что останется.</summary>
        Reviewed,

        /// <summary>Идёт удаление.</summary>
        Deleting,

        /// <summary>
        /// Удаление завершено (успешно, с остановкой или с ошибкой) — показан итог. Отсюда можно сразу строить новый
        /// предпросмотр.
        /// </summary>
        Finished,
    }

    /// <summary>
    /// Экран очистки календаря: объём → «Показать, что будет удалено» → проверка списка → «Удалить» с
    /// подтверждением → ход работы с остановкой → итог.
    ///
    /// Правила безопасности, которые держит модель:
    ///  - удалить можно только по готовому предпросмотру, построенному для текущих параметров: смена объёма,
    ///    периода или подключения сбрасывает предпросмотр;
    ///  - удаление подтверждается вводом адреса ящика (окно подтверждения), окончательное — ещё и отметкой;
    ///  - после удаления предпросмотр израсходован: для нового удаления нужен новый предпросмотр;
    ///  - пока идёт операция, параметры и повторный запуск недоступны; «Остановить» прерывает чтение сразу,
    ///    а удаление — после текущего пакета.
    /// Все операции — асинхронные, окно не замирает; тексты ошибок — из <see cref="ErrorMessages"/>.
    /// </summary>
    public sealed class CleanupViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;
        private readonly Func<DateTime> _today;

        private ITargetSession? _session;
        private CleanupStep _step = CleanupStep.Choose;
        private bool _isWholeCalendar;
        private DateTime? _periodStart;
        private DateTime? _periodEnd;
        private bool _hardDelete;
        private string? _validationError;
        private CleanupPreview? _preview;
        private int _singleCount;
        private int _wholeSeriesCount;
        private IReadOnlyList<PlanRow> _deleteRows = Array.Empty<PlanRow>();
        private IReadOnlyList<PlanRow> _keptRows = Array.Empty<PlanRow>();
        private string _summary = string.Empty;
        private string _progressText = string.Empty;
        private int _progressValue;
        private int _progressMaximum;
        private bool _isStopping;
        private string? _resultTitle;
        private string? _resultText;
        private bool _resultIsSuccess;
        private string? _manifestPath;
        private UserMessage? _error;
        private CancellationTokenSource? _cancellation;
        private Task _operation = Task.CompletedTask;

        /// <param name="dialogs">Окна подтверждения и системные действия.</param>
        /// <param name="today">Текущая дата (для подсказок периода); в тестах — фиксированная.</param>
        public CleanupViewModel(IDialogService dialogs, Func<DateTime>? today = null)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _today = today ?? (() => System.DateTime.Today);

            // По умолчанию — последний месяц: самый частый случай (свежий «мусор»). Это лишь начальное значение полей:
            // без предпросмотра и подтверждения ничего не удаляется.
            _periodStart = _today().AddMonths(-1);
            _periodEnd = _today();

            PreviewCommand = new AsyncCommand(() => Track(PreviewAsync()), () => CanPreview);
            DeleteCommand = new AsyncCommand(() => Track(DeleteAsync()), () => CanDelete);
            StopCommand = new RelayCommand(Stop, () => IsBusy && !_isStopping);
            ShowManifestCommand = new RelayCommand(
                () => _dialogs.ShowFileInFolder(_manifestPath!), () => _manifestPath != null);
            SetLastMonthCommand = new RelayCommand(() => SetPeriod(_today().AddMonths(-1), _today()), () => !IsBusy);
            SetThisYearCommand = new RelayCommand(() => SetPeriod(new DateTime(_today().Year, 1, 1), _today()), () => !IsBusy);
        }

        public AsyncCommand PreviewCommand { get; }

        public AsyncCommand DeleteCommand { get; }

        public RelayCommand StopCommand { get; }

        public RelayCommand ShowManifestCommand { get; }

        public RelayCommand SetLastMonthCommand { get; }

        public RelayCommand SetThisYearCommand { get; }

        // ---- Подключение ----

        /// <summary>Есть ли подключение — без него экран показывает приглашение настроить его.</summary>
        public bool HasSession => _session != null;

        public bool IsDemo => _session?.IsDemo ?? false;

        public string TimeZoneText => _session is null ? string.Empty : RussianText.Zone(_session.TimeZone);

        /// <summary>
        /// Новое подключение (или его отсутствие). Предпросмотр прежнего подключения сбрасывается: удалить можно
        /// только то, что показано для текущего ящика. Во время операции подключение не меняется.
        /// </summary>
        public void SetSession(ITargetSession? session)
        {
            if (IsBusy)
            {
                throw new InvalidOperationException("Нельзя сменить подключение во время операции.");
            }

            _session = session;
            StartOver();
            OnPropertyChanged(nameof(HasSession));
            OnPropertyChanged(nameof(IsDemo));
            OnPropertyChanged(nameof(TimeZoneText));
            RefreshCommands();
        }

        /// <summary>Сообщение об ошибке, случившейся вне операции очистки (например, со списком ящиков).</summary>
        internal void ShowError(UserMessage message) => Error = message;

        // ---- Параметры ----

        public bool IsWholeCalendar
        {
            get => _isWholeCalendar;
            set
            {
                if (SetProperty(ref _isWholeCalendar, value))
                {
                    OnPropertyChanged(nameof(IsPeriod));
                    ParametersChanged();
                }
            }
        }

        public bool IsPeriod
        {
            get => !_isWholeCalendar;
            set => IsWholeCalendar = !value;
        }

        /// <summary>Первый день периода (местная дата приёмника, включительно).</summary>
        public DateTime? PeriodStart
        {
            get => _periodStart;
            set
            {
                if (SetProperty(ref _periodStart, value?.Date))
                {
                    ParametersChanged();
                }
            }
        }

        /// <summary>Последний день периода (включительно: «по 10-е» — весь день 10-го).</summary>
        public DateTime? PeriodEnd
        {
            get => _periodEnd;
            set
            {
                if (SetProperty(ref _periodEnd, value?.Date))
                {
                    ParametersChanged();
                }
            }
        }

        /// <summary>Окончательное удаление вместо переноса в «Удалённые».</summary>
        public bool HardDelete
        {
            get => _hardDelete;
            set => SetProperty(ref _hardDelete, value);
        }

        /// <summary>Что не так с параметрами (показывается под полями); <c>null</c> — всё в порядке.</summary>
        public string? ValidationError
        {
            get => _validationError;
            private set => SetProperty(ref _validationError, value);
        }

        // ---- Состояние ----

        public CleanupStep Step
        {
            get => _step;
            private set
            {
                if (SetProperty(ref _step, value))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(HasPreview));
                    OnPropertyChanged(nameof(HasResult));
                    RefreshCommands();
                }
            }
        }

        public bool IsBusy => _step == CleanupStep.Previewing || _step == CleanupStep.Deleting;

        public bool IsIdle => !IsBusy;

        public bool HasPreview => _step == CleanupStep.Reviewed && _preview != null;

        public bool HasResult => _step == CleanupStep.Finished;

        // ---- Предпросмотр ----

        /// <summary>Одной строкой: что будет удалено.</summary>
        public string Summary
        {
            get => _summary;
            private set => SetProperty(ref _summary, value);
        }

        /// <summary>Причина, по которой удалять нельзя (неполные данные); <c>null</c> — можно.</summary>
        public string? BlockReason => _preview?.BlockReason;

        public bool IsBlocked => _preview != null && !_preview.CanProceed;

        public bool NothingToDelete => _preview != null && _preview.CanProceed && _preview.Plan.IsEmpty;

        // Счётчики считаются один раз при построении предпросмотра, а не при каждом чтении свойства окном.
        public int SingleCount => _preview is null ? 0 : _singleCount;

        public int WholeSeriesCount => _preview is null ? 0 : _wholeSeriesCount;

        public int InstanceCount => _preview?.Plan.OccurrenceDeleteCount ?? 0;

        public int TrimmedSeriesCount => _preview?.Plan.TrimCount ?? 0;

        public IReadOnlyList<PlanRow> DeleteRows
        {
            get => _deleteRows;
            private set
            {
                if (SetProperty(ref _deleteRows, value))
                {
                    OnPropertyChanged(nameof(DeleteRowCount));
                }
            }
        }

        public int DeleteRowCount => _deleteRows.Count;

        public IReadOnlyList<PlanRow> KeptRows
        {
            get => _keptRows;
            private set
            {
                if (SetProperty(ref _keptRows, value))
                {
                    OnPropertyChanged(nameof(KeptRowCount));
                }
            }
        }

        public int KeptRowCount => _keptRows.Count;

        // ---- Ход работы ----

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

        /// <summary>0 — объём неизвестен (бегущий индикатор).</summary>
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

        // ---- Итог ----

        public string? ResultTitle
        {
            get => _resultTitle;
            private set => SetProperty(ref _resultTitle, value);
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

        public string? ManifestPath
        {
            get => _manifestPath;
            private set
            {
                if (SetProperty(ref _manifestPath, value))
                {
                    ShowManifestCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Ошибка последней операции; <c>null</c> — ошибок нет.</summary>
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
        public Task Operation => _operation;

        /// <summary>Остановить: чтение — сразу, удаление — после текущего пакета.</summary>
        public void Stop()
        {
            if (!IsBusy || _isStopping)
            {
                return;
            }

            _isStopping = true;
            ProgressText = _step == CleanupStep.Deleting
                ? "Останавливаю после текущего пакета…"
                : "Останавливаю…";
            _cancellation?.Cancel();
            StopCommand.RaiseCanExecuteChanged();
        }

        private bool CanPreview => HasSession && !IsBusy;

        private bool CanDelete => HasPreview && _preview!.CanProceed && !_preview.Plan.IsEmpty && !IsBusy;

        private Task Track(Task operation)
        {
            _operation = operation;
            return operation;
        }

        private void SetPeriod(DateTime start, DateTime end)
        {
            IsWholeCalendar = false;
            PeriodStart = start;
            PeriodEnd = end;
        }

        private void ParametersChanged()
        {
            ValidationError = null;
            if (_step == CleanupStep.Reviewed)
            {
                ClearPreview();
                Step = CleanupStep.Choose;
            }
        }

        private void StartOver()
        {
            ClearPreview();
            ClearResult();
            Step = CleanupStep.Choose;
        }

        private async Task PreviewAsync()
        {
            ITargetSession session = _session!;
            if (!TryBuildScope(session.TimeZone, out CleanupScope? scope))
            {
                return;
            }

            ClearPreview();
            ClearResult();
            Step = CleanupStep.Previewing;
            using (var cancellation = BeginOperation())
            {
                try
                {
                    CleanupPreview preview = await session.Runner
                        .PreviewAsync(scope!, CleanupFilter.AllEvents, NewProgress(), cancellation.Token)
                        .ConfigureAwait(true);

                    // Тысячи строк готовятся вне потока окна — список появляется без подвисания.
                    TargetTimeZone zone = session.TimeZone;
                    (IReadOnlyList<PlanRow> delete, IReadOnlyList<PlanRow> kept) =
                        await Task.Run(() => BuildRows(preview.Plan, zone), cancellation.Token).ConfigureAwait(true);

                    // «Остановить» во время подготовки строк — предпросмотр не открывается.
                    cancellation.Token.ThrowIfCancellationRequested();

                    _preview = preview;
                    _singleCount = preview.Plan.ToDelete.Count(i => i.Kind == ItemKind.Single);
                    _wholeSeriesCount = preview.Plan.ToDelete.Count - _singleCount;
                    DeleteRows = delete;
                    KeptRows = kept;
                    Summary = DescribePlan(preview.Plan);
                    RaisePreviewProperties();
                    Step = CleanupStep.Reviewed;
                }
                catch (OperationCanceledException)
                {
                    Step = CleanupStep.Choose;
                }
#pragma warning disable CA1031 // Любая ошибка чтения показывается пользователю; удаления при чтении нет.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Error = session.Describe(ex);
                    Step = CleanupStep.Choose;
                }
                finally
                {
                    EndOperation();
                }
            }
        }

        private async Task DeleteAsync()
        {
            ITargetSession session = _session!;
            CleanupPreview preview = _preview!;
            var confirmation = new DeletionConfirmationViewModel(
                session.TargetLabel, session.ConfirmationPhrase, DescribePlan(preview.Plan), _hardDelete);
            if (!_dialogs.ConfirmDeletion(confirmation))
            {
                return;
            }

            TargetDeleteMode mode = _hardDelete ? TargetDeleteMode.HardDelete : TargetDeleteMode.MoveToDeletedItems;
            ClearResult();
            Step = CleanupStep.Deleting;
            using (var cancellation = BeginOperation())
            {
                try
                {
                    CleanupResult result = await session.Runner
                        .RunAsync(preview, mode, NewProgress(), cancellation.Token)
                        .ConfigureAwait(true);
                    ShowResult(result, mode, session);
                }
                catch (OperationCanceledException)
                {
                    ShowStoppedBeforeDeletion();
                }
#pragma warning disable CA1031 // Ошибка до начала удаления: ничего не удалено, показываем причину.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ResultIsSuccess = false;
                    ResultTitle = "Удаление не выполнялось";
                    ResultText = "Ошибка случилась при сверке с сервером, до начала удаления, — ничего не удалено.";
                    Error = session.Describe(ex);
                }
                finally
                {
                    // Предпросмотр израсходован: следующее удаление — только по новому предпросмотру.
                    ClearPreview();
                    Step = CleanupStep.Finished;
                    EndOperation();
                }
            }
        }

        private bool TryBuildScope(TargetTimeZone zone, out CleanupScope? scope)
        {
            scope = null;
            if (_isWholeCalendar)
            {
                scope = CleanupScope.WholeCalendar();
                return true;
            }

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

            (DateTime startUtc, DateTime endUtc) = zone.PeriodFromLocalDates(_periodStart.Value, _periodEnd.Value);
            scope = CleanupScope.Period(startUtc, endUtc);
            return true;
        }

        private CancellationTokenSource BeginOperation()
        {
            Error = null;
            _isStopping = false;
            ProgressText = "Подключаюсь…";
            ProgressValue = 0;
            ProgressMaximum = 0;
            _cancellation = new CancellationTokenSource();
            RefreshCommands();
            return _cancellation;
        }

        private void EndOperation()
        {
            _cancellation = null;
            _isStopping = false;
            ProgressText = string.Empty;
            RefreshCommands();
        }

        /// <summary>
        /// Сообщения о ходе. <c>Progress&lt;T&gt;</c> создаётся в потоке окна и доставляет сообщения в него же. После
        /// нажатия «Остановить» текст «Останавливаю…» не перезаписывается, а числа продолжают обновляться.
        /// </summary>
        private IProgress<CleanupProgress> NewProgress()
        {
            return new Progress<CleanupProgress>(p =>
            {
                ProgressMaximum = p.Total;
                ProgressValue = p.IsDeterminate ? Math.Min(p.Completed, p.Total) : 0;
                if (!_isStopping)
                {
                    ProgressText = DescribeProgress(p);
                }
            });
        }

        internal static string DescribeProgress(CleanupProgress progress)
        {
            switch (progress.Stage)
            {
                case CleanupStage.ReadingCalendar:
                    return progress.IsDeterminate
                        ? $"Читаю календарь: {RussianText.Number(progress.Completed)} из {RussianText.Number(progress.Total)}"
                        : "Читаю календарь…";
                case CleanupStage.ReadingSeries:
                    return "Читаю повторяющиеся серии…";
                case CleanupStage.ReadingPeriod:
                    return "Проверяю экземпляры серий в периоде…";
                case CleanupStage.WritingManifest:
                    return "Записываю список удаляемого…";
                case CleanupStage.Deleting:
                    return $"Удаляю: {RussianText.Number(progress.Completed)} из {RussianText.Number(progress.Total)}";
                default:
                    return string.Empty;
            }
        }

        /// <summary>«1 234 события, 5 серий целиком и 17 экземпляров серий» или «Удалять нечего».</summary>
        internal static string DescribePlan(CleanupPlan plan)
        {
            int singles = plan.ToDelete.Count(i => i.Kind == ItemKind.Single);
            int series = plan.ToDelete.Count(i => i.Kind == ItemKind.RecurringMaster);
            var parts = new List<string>();
            if (singles > 0)
            {
                parts.Add(RussianText.Count(singles, "событие", "события", "событий"));
            }

            if (series > 0)
            {
                parts.Add(RussianText.Count(series, "серия целиком", "серии целиком", "серий целиком"));
            }

            if (plan.OccurrenceDeleteCount > 0)
            {
                parts.Add(RussianText.Count(plan.OccurrenceDeleteCount, "экземпляр серии", "экземпляра серий", "экземпляров серий"));
            }

            if (parts.Count == 0)
            {
                return "Удалять нечего";
            }

            return parts.Count == 1
                ? parts[0]
                : string.Join(", ", parts.Take(parts.Count - 1)) + " и " + parts[parts.Count - 1];
        }

        private static (IReadOnlyList<PlanRow> Delete, IReadOnlyList<PlanRow> Kept) BuildRows(CleanupPlan plan, TargetTimeZone zone)
        {
            PlanRow[] delete = CleanupManifestEntry.ListOf(plan)
                .Select(e => new PlanRow(
                    CleanupLabels.Of(e.Kind),
                    e.Subject ?? "(без темы)",
                    RussianText.Moment(e.StartUtc, zone),
                    RussianText.Moment(e.EndUtc, zone)))
                .ToArray();

            // У серий на границе удаляются только экземпляры в периоде — сама серия остаётся.
            IEnumerable<PlanRow> trimmed = plan.BoundarySeriesToTrim.Select(s => new PlanRow(
                "Серия",
                s.Subject ?? "(без темы)",
                RussianText.Moment(s.StartUtc, zone),
                RussianText.Moment(s.EndUtc, zone),
                "Пересекает границу периода — удаляются только экземпляры внутри периода"));
            IEnumerable<PlanRow> skipped = plan.Skipped.Select(s => new PlanRow(
                CleanupLabels.KeptItemKind(s.Item),
                s.Item.Subject ?? "(без темы)",
                s.Item.IsAppointment ? RussianText.Moment(s.Item.StartUtc, zone) : string.Empty,
                s.Item.IsAppointment ? RussianText.Moment(s.Item.EndUtc, zone) : string.Empty,
                CleanupLabels.Of(s.Reason)));

            return (delete, trimmed.Concat(skipped).ToArray());
        }

        private void ShowResult(CleanupResult result, TargetDeleteMode mode, ITargetSession session)
        {
            ManifestPath = result.ManifestPath;
            string done = DescribeDone(result);
            var text = new StringBuilder();

            if (result.Succeeded)
            {
                ResultIsSuccess = true;
                ResultTitle = result.DeletedCount + result.TrimmedCount == 0 ? "Удалять было нечего" : "Готово";
                text.Append(char.ToUpper(done[0], RussianText.Culture)).Append(done.Substring(1));
                AppendUntouched(text, result);
                if (mode == TargetDeleteMode.MoveToDeletedItems && result.DeletedCount + result.TrimmedCount > 0)
                {
                    text.Append("\n\nУдалённое лежит в папке «Удалённые» и занимает место в ящике. Когда убедитесь, что всё "
                        + "верно, очистите эту папку в Outlook.");
                }
            }
            else if (result.Cancelled)
            {
                ResultIsSuccess = false;
                ResultTitle = "Остановлено";
                text.Append("До остановки: ").Append(done);
                text.Append("\nОстальное не тронуто. Чтобы продолжить, постройте предпросмотр и запустите удаление снова.");
            }
            else if (result.Refused)
            {
                ResultIsSuccess = false;
                ResultTitle = "Удаление не выполнялось";
                text.Append(result.Message).Append("\nНичего не удалено.");
            }
            else
            {
                ResultIsSuccess = false;
                ResultTitle = "Удаление прервано ошибкой";
                text.Append("До ошибки: ").Append(done);
                text.Append("\nЗапустите очистку снова — уже удалённое будет пропущено.");
                Error = result.Exception != null
                    ? session.Describe(result.Exception)
                    : ErrorMessages.For(Core.Errors.OperationErrorKind.Unknown, result.Message);
            }

            ResultText = text.ToString();
        }

        private void ShowStoppedBeforeDeletion()
        {
            ResultIsSuccess = false;
            ResultTitle = "Остановлено";
            ResultText = "Остановлено до начала удаления — ничего не удалено.";
        }

        private static string DescribeDone(CleanupResult result)
        {
            if (result.DeletedCount + result.TrimmedCount == 0)
            {
                return "ничего не удалено.";
            }

            // Сервер считает удалённое целиком одним числом — событий и серий вместе.
            var parts = new List<string>();
            if (result.DeletedCount > 0)
            {
                parts.Add(RussianText.Count(
                    result.DeletedCount, "событие или серия целиком", "события или серии целиком", "событий и серий целиком"));
            }

            if (result.TrimmedCount > 0)
            {
                parts.Add(RussianText.Count(result.TrimmedCount, "экземпляр серии", "экземпляра серий", "экземпляров серий"));
            }

            return "удалено " + string.Join(" и ", parts) + ".";
        }

        private static void AppendUntouched(StringBuilder text, CleanupResult result)
        {
            if (result.NewSincePreviewCount > 0)
            {
                text.Append("\nПоявились после предпросмотра и не тронуты: ").Append(RussianText.Number(result.NewSincePreviewCount)).Append('.');
            }

            if (result.ChangedSincePreviewCount > 0)
            {
                text.Append("\nИзменились или исчезли после предпросмотра и не тронуты: ")
                    .Append(RussianText.Number(result.ChangedSincePreviewCount)).Append('.');
            }
        }

        private void ClearPreview()
        {
            _preview = null;
            DeleteRows = Array.Empty<PlanRow>();
            KeptRows = Array.Empty<PlanRow>();
            Summary = string.Empty;
            RaisePreviewProperties();
        }

        private void ClearResult()
        {
            ResultTitle = null;
            ResultText = null;
            ResultIsSuccess = false;
            ManifestPath = null;
            Error = null;
        }

        private void RaisePreviewProperties()
        {
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(BlockReason));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(NothingToDelete));
            OnPropertyChanged(nameof(SingleCount));
            OnPropertyChanged(nameof(WholeSeriesCount));
            OnPropertyChanged(nameof(InstanceCount));
            OnPropertyChanged(nameof(TrimmedSeriesCount));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            PreviewCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            SetLastMonthCommand.RaiseCanExecuteChanged();
            SetThisYearCommand.RaiseCanExecuteChanged();
        }
    }
}
