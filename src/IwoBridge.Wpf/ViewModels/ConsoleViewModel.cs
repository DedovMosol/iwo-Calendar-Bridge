using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using IwoBridge.App.History;
using IwoBridge.Wpf.Mvvm;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Панель «Консоль» внизу главного окна: те же строки, что программа пишет в консоль Windows, — запуск, начало,
    /// предпросмотр и итог каждой операции, подробности сбоев, Outlook, изменения учётных записей. Строки приходят из
    /// журнала (<see cref="MirroredOperationLog"/>) из любого потока и добавляются в потоке окна. Хранится не больше
    /// <see cref="MaxLines"/> строк: старые уходят, память не растёт при работе по расписанию неделями.
    /// </summary>
    public sealed class ConsoleViewModel : ObservableObject
    {
        /// <summary>Сколько последних строк держит панель.</summary>
        public const int MaxLines = 1000;

        private readonly SynchronizationContext? _context;
        private bool _isExpanded = true;

        /// <summary>Создавать в потоке окна: строки из других потоков передаются в него.</summary>
        public ConsoleViewModel()
        {
            _context = SynchronizationContext.Current;
            Lines = new ObservableCollection<OperationLine>();
            ClearCommand = new RelayCommand(Clear, () => Lines.Count > 0);
            ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        }

        public ObservableCollection<OperationLine> Lines { get; }

        public bool HasLines => Lines.Count > 0;

        /// <summary>Очистить панель (журнал и консоль Windows не меняются).</summary>
        public RelayCommand ClearCommand { get; }

        /// <summary>Свернуть или развернуть панель.</summary>
        public RelayCommand ToggleCommand { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ToggleText));
                }
            }
        }

        public string ToggleText => _isExpanded ? "Свернуть" : "Развернуть";

        /// <summary>Весь текст панели — для «Копировать всё».</summary>
        public string AllText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

        /// <summary>Добавить строку; из любого потока. Не бросает исключений: журнал не прерывает операции.</summary>
        public void Append(OperationLine line)
        {
            if (line is null)
            {
                return;
            }

            if (_context != null && SynchronizationContext.Current != _context)
            {
                _context.Post(_ => Add(line), null);
                return;
            }

            Add(line);
        }

        private void Add(OperationLine line)
        {
            Lines.Add(line);
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }

            Changed();
        }

        private void Clear()
        {
            Lines.Clear();
            Changed();
        }

        private void Changed()
        {
            OnPropertyChanged(nameof(HasLines));
            ClearCommand.RaiseCanExecuteChanged();
        }
    }
}
