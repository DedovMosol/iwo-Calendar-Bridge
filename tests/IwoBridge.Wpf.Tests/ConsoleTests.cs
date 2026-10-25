using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using IwoBridge.App.History;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Панель «Консоль» внизу окна: строки журнала (начало, предпросмотр, итог, подробности сбоев) с видом для цвета,
    /// строки из другого потока — в поток окна, не больше <see cref="ConsoleViewModel.MaxLines"/> строк, очистка,
    /// сворачивание; в настоящем окне — абзацы по строкам и строка сетки по состоянию панели.
    /// </summary>
    public sealed class ConsoleTests
    {
        /// <summary>Модель без контекста синхронизации: строки добавляются сразу (в тестах без потока окна).</summary>
        private static ConsoleViewModel NewConsole()
        {
            SynchronizationContext? saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return new ConsoleViewModel();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }
        }

        [Fact]
        public async Task DemoCopy_PreviewStartAndResultInConsole_ResultIsSuccessLine()
        {
            ConsoleViewModel console = NewConsole();
            var log = new MirroredOperationLog(new MemoryOperationLog(), console.Append);
            var dialogs = new FakeDialogs { AskAnswer = true };
            var vm = new MainViewModel(new DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs, new DemoSources(), log: log, console: console);
            Assert.Same(console, vm.Console);

            await vm.Copy.PreviewCommand.ExecuteAsync();
            await vm.Copy.CopyCommand.ExecuteAsync();

            string[] texts = console.Lines.Select(l => l.Text).ToArray();
            Assert.Contains(texts, t => t.Contains("Outlook: календарей в списке"));
            Assert.Contains(texts, t => t.Contains("Предпросмотр копирования: ") && t.Contains("создать"));
            Assert.Contains(texts, t => t.Contains("Копирование начато"));
            OperationLine result = console.Lines.Last();
            Assert.Equal(OperationLineKind.Success, result.Kind);
            Assert.Contains("— готово", result.Text);
            Assert.Contains("→", result.Text); // в окне — типографские знаки, ASCII-замены только для консоли Windows
        }

        [Fact]
        public void ProblemRecord_LineAndDetails_ColoredKinds()
        {
            ConsoleViewModel console = NewConsole();
            var log = new MirroredOperationLog(new MemoryOperationLog(), console.Append);

            log.Info("Предпросмотр очистки не построен", isProblem: true);
            log.Record(new OperationRecord(
                OperationKind.Copy, DateTime.UtcNow.AddSeconds(-3), DateTime.UtcNow, OperationOutcome.Problem, "Outlook: Календарь",
                "box@example.test на mail.example.test", "01.01.2026 – 31.01.2026", 1, 0, 0, 1, "Готово, но не всё",
                new[] { "Не удалось (1):", "• Планёрка — сервер занят" }));

            Assert.Equal(
                new[] { OperationLineKind.Problem, OperationLineKind.Problem, OperationLineKind.Detail, OperationLineKind.Detail },
                console.Lines.Select(l => l.Kind));
            Assert.True(console.ClearCommand.CanExecute(null));
            console.ClearCommand.Execute(null);
            Assert.False(console.HasLines);
        }

        [Fact]
        public void KeepsLastMaxLines()
        {
            ConsoleViewModel console = NewConsole();
            for (int i = 0; i < ConsoleViewModel.MaxLines + 25; i++)
            {
                console.Append(new OperationLine(OperationLineKind.Info, "строка " + i));
            }

            Assert.Equal(ConsoleViewModel.MaxLines, console.Lines.Count);
            Assert.Equal("строка 25", console.Lines[0].Text);
        }

        [Fact]
        public async Task LineFromOtherThread_PostedToWindowContext()
        {
            var context = new QueueContext();
            SynchronizationContext? saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            ConsoleViewModel console;
            try
            {
                console = new ConsoleViewModel();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }

            await Task.Run(() => console.Append(new OperationLine(OperationLineKind.Info, "из пула")));

            Assert.Empty(console.Lines);
            context.RunAll();
            Assert.Equal("из пула", Assert.Single(console.Lines).Text);
        }

        [Fact]
        public void MainWindow_ConsoleShowsLines_CollapseKeepsHeader_ExpandRestoresHeight()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs();
                    var console = new ConsoleViewModel();
                    var vm = new MainViewModel(new DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs, console: console);
                    console.Append(new OperationLine(OperationLineKind.Info, "2026-10-25 14:30:00  Копирование начато"));
                    console.Append(new OperationLine(OperationLineKind.Problem, "2026-10-25 14:30:05  Копирование — есть проблемы\nвторая строка"));
                    var window = new MainWindow(vm, dialogs);
                    try
                    {
                        Visuals.ShowOffscreen(window, 1120, 780);
                        await Visuals.Settle(window);

                        var output = Visuals.ByAutomationId<RichTextBox>(window, "ConsoleOutput");
                        Assert.True(output.IsVisible);
                        Assert.Equal(2, output.Document.Blocks.Count);
                        string text = new TextRange(output.Document.ContentStart, output.Document.ContentEnd).Text;
                        Assert.Contains("Копирование начато", text);
                        Assert.Contains("вторая строка", text);

                        console.Append(new OperationLine(OperationLineKind.Success, "2026-10-25 14:31:00  готово"));
                        await Visuals.Settle(window);
                        Assert.Equal(3, output.Document.Blocks.Count);

                        var row = (RowDefinition)window.FindName("ConsoleRow");
                        double expanded = row.ActualHeight;
                        Assert.InRange(expanded, 100, 200);

                        console.ToggleCommand.Execute(null);
                        await Visuals.Settle(window);
                        Assert.False(output.IsVisible);
                        Assert.True(row.ActualHeight < 50, $"свёрнутая консоль {row.ActualHeight}");
                        Assert.Equal("Развернуть", console.ToggleText);

                        console.ToggleCommand.Execute(null);
                        await Visuals.Settle(window);
                        Assert.True(output.IsVisible);
                        Assert.Equal(expanded, row.ActualHeight, 1);
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        /// <summary>Контекст «потока окна», который выполняет переданное по команде теста.</summary>
        private sealed class QueueContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new Queue<(SendOrPostCallback, object?)>();

            public override void Post(SendOrPostCallback d, object? state)
            {
                lock (_queue)
                {
                    _queue.Enqueue((d, state));
                }
            }

            public void RunAll()
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) next;
                    lock (_queue)
                    {
                        if (_queue.Count == 0)
                        {
                            return;
                        }

                        next = _queue.Dequeue();
                    }

                    next.Callback(next.State);
                }
            }
        }
    }
}
