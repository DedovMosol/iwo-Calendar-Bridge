using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using IwoBridge.App.History;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Строка окна «История»: одна операция.</summary>
    public sealed class HistoryRow
    {
        internal HistoryRow(OperationRecord record)
        {
            Record = record;
            DateTime started = record.StartedUtc.ToLocalTime();
            When = started.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            Operation = OperationRecordText.KindName(record.Kind);
            Route = OperationRecordText.Route(record);
            Outcome = record.Outcome == OperationOutcome.Success ? "Готово"
                : record.Outcome == OperationOutcome.Cancelled ? "Остановлено"
                : "Есть проблемы";
            Duration = OperationRecordText.Duration(record.Duration);
            Details = string.Join(
                Environment.NewLine,
                new[]
                {
                    Operation + ", " + started.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " (" + Duration + ")",
                    Route,
                    record.Period,
                    record.Title,
                }
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Concat(record.Details.Count == 0 ? Array.Empty<string>() : new[] { string.Empty })
                    .Concat(record.Details));
        }

        internal OperationRecord Record { get; }

        public string When { get; }

        public string Operation { get; }

        public string Route { get; }

        public string Period => Record.Period;

        public int Created => Record.Created;

        public int Updated => Record.Updated;

        public int Deleted => Record.Deleted;

        public int Failed => Record.Failed;

        public string Outcome { get; }

        public bool IsProblem => Record.Outcome == OperationOutcome.Problem;

        public string Duration { get; }

        public string Title => Record.Title;

        /// <summary>Всё о записи — для панели подробностей и копирования в буфер обмена.</summary>
        public string Details { get; }
    }

    /// <summary>
    /// Окно «История»: журнал операций — когда, какая операция, откуда и куда, за какой период, сколько создано,
    /// перезаписано, удалено, сколько не удалось и чем кончилось. Новая операция появляется сверху сразу, пока окно открыто.
    /// </summary>
    public sealed class HistoryViewModel : ObservableObject, IDisposable
    {
        /// <summary>Сколько последних операций показывать: больше человеку не нужно, а окно открывается мгновенно.</summary>
        public const int MaxRows = 1000;

        private readonly IOperationLog _log;
        private readonly SynchronizationContext? _context;

        // Все прочитанные и добавленные записи, новые первыми: итог за 7 дней считается по ним, а не по показанным строкам
        // (автокопирование раз в 5 минут даёт за неделю больше двух тысяч записей).
        private readonly List<OperationRecord> _records = new List<OperationRecord>();
        private HistoryRow? _selected;

        public HistoryViewModel(IOperationLog log, IDialogService dialogs)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            if (dialogs is null)
            {
                throw new ArgumentNullException(nameof(dialogs));
            }

            _context = SynchronizationContext.Current;
            Rows = new ObservableCollection<HistoryRow>();
            RefreshCommand = new RelayCommand(Reload);
            ShowFileCommand = new RelayCommand(() => dialogs.ShowFileInFolder(_log.Location!), () => _log.Location != null);
            Reload();
            _log.Recorded += OnRecorded;
        }

        public ObservableCollection<HistoryRow> Rows { get; }

        public RelayCommand RefreshCommand { get; }

        public RelayCommand ShowFileCommand { get; }

        public HistoryRow? Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }

        public bool IsEmpty => Rows.Count == 0;

        /// <summary>Где лежит журнал (для подсказки); в демонстрационном режиме — только в памяти.</summary>
        public string LocationText => _log.Location is null
            ? "Демонстрационный режим: журнал только в памяти, до закрытия программы."
            : "Журнал: " + _log.Location;

        /// <summary>
        /// Итог за последние 7 дней одной строкой. Журнал ограничен размером (частые запуски с большими итогами помещаются
        /// в него меньше чем за неделю): если самая старая запись моложе недели, итог честно начинается с неё — «С 23.10.2026 14:30».
        /// </summary>
        public string Summary
        {
            get
            {
                DateTime since = DateTime.UtcNow.AddDays(-7);
                OperationRecord[] week = _records.Where(r => r.StartedUtc >= since).ToArray();
                DateTime? oldest = _records.Count == 0 ? (DateTime?)null : _records.Min(r => r.StartedUtc);
                string label = oldest.HasValue && oldest.Value > since
                    ? "С " + oldest.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                    : "За 7 дней";
                return label + ": операций " + RussianText.Number(week.Length)
                    + ", создано " + RussianText.Number(week.Sum(r => r.Created))
                    + ", перезаписано " + RussianText.Number(week.Sum(r => r.Updated))
                    + ", удалено " + RussianText.Number(week.Sum(r => r.Deleted))
                    + ", не удалось " + RussianText.Number(week.Sum(r => r.Failed))
                    + ", с проблемами " + RussianText.Number(week.Count(r => r.Outcome == OperationOutcome.Problem)) + ".";
            }
        }

        /// <summary>Окно закрыли — модель больше не слушает журнал (журнал живёт дольше окна).</summary>
        public void Dispose()
        {
            _log.Recorded -= OnRecorded;
        }

        private void Reload()
        {
            _records.Clear();
            _records.AddRange(_log.Recent(int.MaxValue));
            Rows.Clear();
            foreach (OperationRecord record in _records.Take(MaxRows))
            {
                Rows.Add(new HistoryRow(record));
            }

            Selected = Rows.FirstOrDefault();
            RaiseTotals();
        }

        private void OnRecorded(object? sender, OperationRecord record)
        {
            if (_context != null && SynchronizationContext.Current != _context)
            {
                _context.Post(_ => Add(record), null);
                return;
            }

            Add(record);
        }

        private void Add(OperationRecord record)
        {
            _records.Insert(0, record);
            Rows.Insert(0, new HistoryRow(record));
            while (Rows.Count > MaxRows)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }

            RaiseTotals();
        }

        private void RaiseTotals()
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }
    }
}
