using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace IwoBridge.App.History
{
    /// <summary>
    /// Журнал с копией в консоль: каждая запись и каждое сообщение о ходе — строкой в <see cref="TextWriter"/> (консоль,
    /// из которой запустили программу, или файл, куда перенаправлен её вывод), знаки — <see cref="OperationRecordText.Plain"/>.
    /// Хранение — у вложенного журнала.
    /// </summary>
    public sealed class MirroredOperationLog : IOperationLog
    {
        private readonly IOperationLog _inner;
        private readonly TextWriter _writer;
        private readonly object _sync = new object();

        public MirroredOperationLog(IOperationLog inner, TextWriter writer)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
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
            Write(OperationRecordText.Line(record));
            foreach (string detail in record.Outcome == OperationOutcome.Success ? Enumerable.Empty<string>() : record.Details.Take(10))
            {
                Write("    " + detail.Replace("\n", "\n    "));
            }
        }

        public void Info(string message)
        {
            _inner.Info(message);
            Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
        }

        public IReadOnlyList<OperationRecord> Recent(int max) => _inner.Recent(max);

        /// <summary>Консоль закрыли или вывод оборвался — программа работает дальше без консоли.</summary>
        private void Write(string line)
        {
            lock (_sync)
            {
                try
                {
                    _writer.WriteLine(OperationRecordText.Plain(line));
                    _writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
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

        public void Info(string message)
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
