using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Интервал автокопирования в списке: «30 минут», «1 час», «сутки».</summary>
    public sealed class IntervalOption
    {
        internal IntervalOption(int minutes)
        {
            Minutes = minutes;

            // Читается после «раз в»: «раз в 5 минут», «раз в час», «раз в сутки».
            Label = minutes == 1440 ? "сутки"
                : minutes == 60 ? "час"
                : minutes % 60 == 0 ? RussianText.Count(minutes / 60, "час", "часа", "часов")
                : RussianText.Count(minutes, "минуту", "минуты", "минут");
        }

        public int Minutes { get; }

        public string Label { get; }
    }

    /// <summary>Итог одного автоматического запуска — для строки состояния и уведомления.</summary>
    public sealed class AutoRunOutcome
    {
        /// <param name="needsAttention">Ошибка, которую исправит только человек (вход, права): расписание приостанавливается.</param>
        public AutoRunOutcome(string summary, bool isProblem, bool skipped = false, bool needsAttention = false)
        {
            Summary = summary;
            IsProblem = isProblem || needsAttention;
            Skipped = skipped;
            NeedsAttention = needsAttention;
        }

        public bool NeedsAttention { get; }

        /// <summary>Коротко: «создано 2, удалено копий 1» или «нет связи с сервером».</summary>
        public string Summary { get; }

        /// <summary>Ошибка или копирование не выполнено (нужно внимание человека).</summary>
        public bool IsProblem { get; }

        /// <summary>Не запускалось: идёт другая операция — повторится через минуту.</summary>
        public bool Skipped { get; }
    }

    /// <summary>
    /// Автокопирование «раз в N»: включение, интервал, автозапуск с Windows, состояние («следующее в…, последнее…»).
    /// Само копирование выполняет вкладка копирования (<c>run</c>) — здесь только расписание (SoC).
    ///
    /// Правила расписания:
    ///  - следующий запуск назначается после окончания текущего — запуски не накладываются;
    ///  - первый запуск — через минуту после включения или старта программы;
    ///  - идёт другая операция или открыт вопрос — повтор через минуту, а не через интервал;
    ///  - ошибка, которую исправит только человек (неверный пароль, нет прав, чужой сертификат), — расписание
    ///    приостанавливается до правки подключения (<see cref="Resume"/>): повтор неверного входа каждые N минут
    ///    заблокировал бы учётную запись;
    ///  - о проблемах сообщает <see cref="Problem"/> — окно показывает уведомление (одно на одну и ту же проблему);
    ///    об успешных запусках — только строка состояния, без всплывающих окон.
    /// </summary>
    public sealed class AutoCopyViewModel : ObservableObject, IDisposable
    {
        internal static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(1);

        private readonly IOneShotTimer _timer;
        private readonly IAutostart? _autostart;
        private readonly Func<Task<AutoRunOutcome>> _run;
        private readonly Action _changed;
        private readonly Func<DateTime> _now;
        private bool _isEnabled;
        private IntervalOption _interval;
        private DateTime? _nextRun;
        private DateTime? _lastRun;
        private string? _lastSummary;
        private bool _lastIsProblem;
        private bool _isRunning;
        private string? _autostartError;
        private string? _pausedReason;
        private string? _lastProblem;

        /// <param name="run">Один автоматический запуск копирования.</param>
        /// <param name="changed">Настройки изменились — сохранить.</param>
        public AutoCopyViewModel(
            IOneShotTimer timer, IAutostart? autostart, Func<Task<AutoRunOutcome>> run, Action changed, bool enabled, int intervalMinutes, Func<DateTime>? now = null)
        {
            _timer = timer ?? throw new ArgumentNullException(nameof(timer));
            _autostart = autostart;
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
            _now = now ?? (() => DateTime.Now);
            Intervals = CopySettings.Intervals.Select(m => new IntervalOption(m)).ToArray();
            _interval = Intervals.FirstOrDefault(i => i.Minutes == intervalMinutes) ?? Intervals.First(i => i.Minutes == 30);
            _timer.Elapsed += OnElapsed;
            RunNowCommand = new AsyncCommand(RunAsync, () => _isEnabled && !_isRunning && _pausedReason is null);
            if (enabled)
            {
                _isEnabled = true;
                Arm(FirstDelay);
            }
        }

        /// <summary>Что-то пошло не так при автоматическом запуске — окно показывает уведомление.</summary>
        public event EventHandler<AutoRunOutcome>? Problem;

        public IReadOnlyList<IntervalOption> Intervals { get; }

        /// <summary>Скопировать сейчас, не дожидаясь расписания (меню значка в области уведомлений).</summary>
        public AsyncCommand RunNowCommand { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (!SetProperty(ref _isEnabled, value))
                {
                    return;
                }

                _pausedReason = null;
                _lastProblem = null;
                if (value)
                {
                    Arm(FirstDelay);
                }
                else
                {
                    _timer.Cancel();
                    _nextRun = null;
                }

                RaiseStatus();
                RunNowCommand.RaiseCanExecuteChanged();
                _changed();
            }
        }

        public IntervalOption Interval
        {
            get => _interval;
            set
            {
                if (value is null || !SetProperty(ref _interval, value))
                {
                    return;
                }

                if (_isEnabled && !_isRunning && _pausedReason is null)
                {
                    // Новый интервал считается от последнего запуска (или от сейчас, если запусков не было).
                    DateTime from = _lastRun ?? _now();
                    TimeSpan due = from + TimeSpan.FromMinutes(value.Minutes) - _now();
                    Arm(due < FirstDelay ? FirstDelay : due);
                }

                RaiseStatus();
                _changed();
            }
        }

        public int IntervalMinutes => _interval.Minutes;

        /// <summary>Автозапуск доступен (в демонстрационном режиме — нет).</summary>
        public bool CanAutostart => _autostart != null;

        public bool StartWithWindows
        {
            get => _autostart?.IsEnabled ?? false;
            set
            {
                if (_autostart is null || _autostart.IsEnabled == value)
                {
                    return;
                }

                try
                {
                    _autostart.Set(value);
                    AutostartError = null;
                }
#pragma warning disable CA1031 // Реестр недоступен (политика) — сообщить в окне, остальное работает.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    AutostartError = "Не удалось изменить автозапуск: " + ex.Message;
                }

                OnPropertyChanged(nameof(StartWithWindows));
            }
        }

        public string? AutostartError
        {
            get => _autostartError;
            private set => SetProperty(ref _autostartError, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    RunNowCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>«Следующее — в 14:35. Последнее — 14:05: создано 2.» Пусто — автокопирование выключено.</summary>
        public string StatusText
        {
            get
            {
                if (!_isEnabled)
                {
                    return string.Empty;
                }

                if (_pausedReason != null)
                {
                    return "Автокопирование приостановлено: " + _pausedReason
                        + ". Исправьте подключение (кнопка «Приёмник…» или «Источник (EWS)…») — копирование продолжится само.";
                }

                var parts = new List<string>();
                if (_isRunning)
                {
                    parts.Add("Копирую…");
                }
                else if (_nextRun.HasValue)
                {
                    parts.Add("Следующее — " + When(_nextRun.Value) + ".");
                }

                if (_lastRun.HasValue)
                {
                    parts.Add("Последнее — " + When(_lastRun.Value) + ": " + _lastSummary + ".");
                }

                return "Автокопирование раз в " + _interval.Label + ". " + string.Join(" ", parts);
            }
        }

        public bool LastIsProblem => _lastIsProblem || _pausedReason != null;

        public bool IsPaused => _pausedReason != null;

        /// <summary>Подключение исправили — снять паузу и скопировать через минуту.</summary>
        public void Resume()
        {
            if (_pausedReason is null)
            {
                return;
            }

            _pausedReason = null;
            _lastProblem = null;
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(LastIsProblem));
            Arm(FirstDelay);
            RaiseStatus();
            RunNowCommand.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            _timer.Elapsed -= OnElapsed;
            _timer.Dispose();
        }

        private async void OnElapsed(object? sender, EventArgs e)
        {
            // async void — только здесь, на границе с таймером; исключения run обрабатывает сам и возвращает итогом.
            await RunAsync().ConfigureAwait(true);
        }

        private async Task RunAsync()
        {
            if (!_isEnabled || _isRunning || _pausedReason != null)
            {
                return;
            }

            _timer.Cancel();
            _nextRun = null;
            IsRunning = true;
            RaiseStatus();
            AutoRunOutcome outcome;
            try
            {
                outcome = await _run().ConfigureAwait(true);
            }
#pragma warning disable CA1031 // Расписание не должно умирать от непредвиденной ошибки одного запуска.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                outcome = new AutoRunOutcome("ошибка: " + ex.Message, isProblem: true);
            }
            finally
            {
                IsRunning = false;
            }

            if (outcome.Skipped)
            {
                Arm(BusyRetry);
            }
            else
            {
                _lastRun = _now();
                _lastSummary = outcome.Summary;
                _lastIsProblem = outcome.IsProblem;
                if (outcome.NeedsAttention)
                {
                    _pausedReason = outcome.Summary; // до правки подключения — без новых попыток входа
                    OnPropertyChanged(nameof(IsPaused));
                }
                else
                {
                    Arm(TimeSpan.FromMinutes(_interval.Minutes));
                }

                OnPropertyChanged(nameof(LastIsProblem));

                // Уведомление — один раз на одну и ту же проблему (закрытый на ночь Outlook не даст десятков окон).
                string? problem = outcome.IsProblem ? outcome.Summary : null;
                if (problem != null && problem != _lastProblem)
                {
                    Problem?.Invoke(this, outcome);
                }

                _lastProblem = problem;
            }

            RaiseStatus();
        }

        private void Arm(TimeSpan due)
        {
            if (!_isEnabled)
            {
                return;
            }

            _nextRun = _now() + due;
            _timer.Schedule(due);
        }

        private string When(DateTime moment) =>
            moment.Date == _now().Date
                ? "в " + moment.ToString("HH:mm", CultureInfo.InvariantCulture)
                : moment.ToString("dd.MM в HH:mm", CultureInfo.InvariantCulture);

        private void RaiseStatus() => OnPropertyChanged(nameof(StatusText));
    }
}
