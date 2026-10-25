using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using IwoBridge.Wpf.Services;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Вывод в консоль через очередь: пока консоль стоит (в её окне выделяют текст, канал не читают), поток окон не
    /// ждёт, строки приходят по порядку, лишнее сверх очереди отбрасывается, выход из программы не зависает.
    /// </summary>
    public sealed class ConsoleOutputTests
    {
        [Fact]
        public void QueuedWriter_OutputStalled_CallerDoesNotWait_LinesInOrder_ExcessDropped()
        {
            using (var gate = new ManualResetEventSlim(false))
            {
                var inner = new StalledWriter(gate);
                var writer = new ConsoleOutput.QueuedWriter(inner);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 1500; i++)
                {
                    writer.WriteLine("строка " + i);
                    writer.Flush();
                }

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "запись ждала остановленный вывод: " + watch.Elapsed);

                gate.Set();
                writer.Dispose();
                string[] lines = inner.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                Assert.InRange(lines.Length, 1000, 1001); // одна строка уже в записи и очередь на 1000
                Assert.Equal(Enumerable.Range(0, lines.Length).Select(i => "строка " + i), lines);
            }
        }

        [Fact]
        public void QueuedWriter_OutputStuckForever_DisposeReturns()
        {
            using (var gate = new ManualResetEventSlim(false))
            {
                var writer = new ConsoleOutput.QueuedWriter(new StalledWriter(gate));
                writer.WriteLine("строка");
                writer.Flush();

                var watch = Stopwatch.StartNew();
                writer.Dispose();

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "выход ждал остановленный вывод: " + watch.Elapsed);
                writer.WriteLine("после выхода");
                writer.Flush(); // опоздавшая строка не роняет программу
                gate.Set();
            }
        }

        /// <summary>Вывод, который стоит, пока не открыт <c>gate</c> (как консоль с выделенным текстом).</summary>
        private sealed class StalledWriter : TextWriter
        {
            private readonly ManualResetEventSlim _gate;
            private readonly StringBuilder _text = new StringBuilder();

            public StalledWriter(ManualResetEventSlim gate) => _gate = gate;

            public override Encoding Encoding => Encoding.Unicode;

            public string Text
            {
                get
                {
                    lock (_text)
                    {
                        return _text.ToString();
                    }
                }
            }

            public override void Write(string? value)
            {
                _gate.Wait();
                lock (_text)
                {
                    _text.Append(value);
                }
            }

            public override void Write(char value) => Write(value.ToString());
        }
    }
}
