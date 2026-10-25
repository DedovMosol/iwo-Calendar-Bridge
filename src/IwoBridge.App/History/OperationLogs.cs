using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IwoBridge.App.History
{
    /// <summary>
    /// Журнал с копией в консоль: каждая запись и каждое сообщение о ходе — строками (<see cref="OperationLine"/>) в
    /// получатель. Получатель — панель консоли в окне программы или <see cref="TextWriter"/> (консоль, из которой
    /// запустили программу, или файл, куда перенаправлен её вывод). Хранение — у вложенного журнала. Декораторы
    /// вкладываются: панель окна и консоль Windows получают одни и те же строки.
    /// </summary>
    public sealed class MirroredOperationLog : IOperationLog
    {
        private readonly IOperationLog _inner;
        private readonly Action<OperationLine> _sink;

        /// <param name="sink">Получатель строк; не должен бросать исключений (журнал не прерывает операции).</param>
        public MirroredOperationLog(IOperationLog inner, Action<OperationLine> sink)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>Строки — в консоль Windows или файл: знаки — <see cref="OperationRecordText.Plain"/>.</summary>
        public MirroredOperationLog(IOperationLog inner, TextWriter writer)
            : this(inner, new WriterSink(writer ?? throw new ArgumentNullException(nameof(writer))).Write)
        {
        }

        public event EventHandler<OperationRecord>? Recorded
        {
            add => _inner.Recorded += value;
            remove => _inner.Recorded -= value;
        }

        public string? Location => _inner.Location;

        public void Record(OperationRecord record)
        {
            _inner.Record(record);
            foreach (OperationLine line in OperationRecordText.Lines(record))
            {
                _sink(line);
            }
        }

        public void Info(string message, bool isProblem = false)
        {
            _inner.Info(message, isProblem);
            _sink(OperationRecordText.InfoLine(DateTime.Now, message, isProblem));
        }

        public IReadOnlyList<OperationRecord> Recent(int max) => _inner.Recent(max);

        /// <summary>Строки в <see cref="TextWriter"/>. Консоль закрыли или вывод оборвался — программа работает дальше без него.</summary>
        private sealed class WriterSink
        {
            private readonly TextWriter _writer;
            private readonly object _sync = new object();

            public WriterSink(TextWriter writer) => _writer = writer;

            public void Write(OperationLine line)
            {
                lock (_sync)
                {
                    try
                    {
                        _writer.WriteLine(OperationRecordText.Plain(line.Text));
                        _writer.Flush();
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }

    /// <summary>Журнал в памяти — демонстрационный режим и тесты: ничего не пишется на диск.</summary>
    public sealed class MemoryOperationLog : IOperationLog
    {
        private readonly List<OperationRecord> _records = new List<OperationRecord>();
        private readonly List<string> _messages = new List<string>();
        private readonly object _sync = new object();

        public event EventHandler<OperationRecord>? Recorded;

        public string? Location => null;

        /// <summary>Сообщения о ходе работы (для тестов).</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_sync)
                {
                    return _messages.ToArray();
                }
            }
        }

        public void Record(OperationRecord record)
        {
            lock (_sync)
            {
                _records.Add(record ?? throw new ArgumentNullException(nameof(record)));
            }

            Recorded?.Invoke(this, record);
        }

        public void Info(string message, bool isProblem = false)
        {
            lock (_sync)
            {
                _messages.Add(message);
            }
        }

        public IReadOnlyList<OperationRecord> Recent(int max)
        {
            lock (_sync)
            {
                return _records.Skip(Math.Max(0, _records.Count - Math.Max(0, max))).Reverse().ToArray();
            }
        }
    }
}
