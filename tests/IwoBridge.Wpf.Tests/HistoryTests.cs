using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using IwoBridge.App.Configuration;
using IwoBridge.App.History;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Журнал операций со стороны экрана: каждое копирование (вручную и по расписанию) и очистка попадают в журнал с
    /// числами и тем же итогом, что на экране; пропуск «идёт другая операция» — нет; окно «История» показывает новые
    /// записи сразу и отписывается при закрытии.
    /// </summary>
    public sealed class HistoryTests
    {
        private static DateTime Utc(int day, int hour) => new DateTime(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        private static SourceItem Item(string id, int day) => new SourceItem(id, "Событие " + id, null, null, Utc(day, 9), Utc(day, 10));

        private static (CopyViewModel Vm, TestSession Target, FakeDialogs Dialogs) Copy(MemoryOperationLog log, params SourceItem[] items)
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            var target = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));
            var source = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()))
            {
                SourceCalendar = new InMemorySource("источник по EWS", items),
                TargetLabel = CopyViewModelTests.SourceMailbox,
            };
            var vm = new CopyViewModel(
                dialogs, null, () => new DateTime(2026, 1, 20), new MemoryCopySettingsStore(new CopySettings(fromOutlook: false)), log: log);
            vm.SetTarget(target);
            vm.SetEwsSource(source);
            return (vm, target, dialogs);
        }

        [Fact]
        public async Task ManualCopy_RecordedWithCountsAndScreenTitle()
        {
            var log = new MemoryOperationLog();
            (CopyViewModel vm, _, _) = Copy(log, Item("a", 10), Item("b", 11));
            await vm.PreviewCommand.ExecuteAsync();

            await vm.CopyCommand.ExecuteAsync();

            OperationRecord record = Assert.Single(log.Recent(10));
            Assert.Equal(OperationKind.Copy, record.Kind);
            Assert.Equal(OperationOutcome.Success, record.Outcome);
            Assert.Equal(2, record.Created);
            Assert.Equal(vm.ResultTitle, record.Title);
            Assert.Equal("источник по EWS", record.Source);
            Assert.Contains("→", OperationRecordText.Route(record));
            Assert.Contains(log.Messages, m => m.StartsWith("Копирование начато", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AutoRun_Recorded_SkippedNot()
        {
            var log = new MemoryOperationLog();
            (CopyViewModel vm, _, FakeDialogs dialogs) = Copy(log, Item("a", 10));
            dialogs.IsShowingDialog = true;
            Assert.True((await vm.RunScheduledAsync()).Skipped);
            Assert.Empty(log.Recent(10));

            dialogs.IsShowingDialog = false;
            await vm.RunScheduledAsync();
            await vm.RunScheduledAsync();

            OperationRecord[] records = log.Recent(10).ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, r => Assert.Equal(OperationKind.AutoCopy, r.Kind));
            Assert.Equal(1, records[1].Created); // старшая запись — первый запуск
            Assert.Equal("изменений нет", records[0].Title);
            Assert.NotEmpty(records[0].Period);
        }

        [Fact]
        public async Task AutoRun_NotPossible_RecordedAsProblem()
        {
            var log = new MemoryOperationLog();
            var vm = new CopyViewModel(new FakeDialogs(), null, () => new DateTime(2026, 1, 20), new MemoryCopySettingsStore(new CopySettings(fromOutlook: false)), log: log);

            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            OperationRecord record = Assert.Single(log.Recent(10));
            Assert.Equal(OperationOutcome.Problem, record.Outcome);
            Assert.Equal(outcome.Summary, record.Title);
        }

        [Fact]
        public async Task Cleanup_RecordedWithDeletedCountAndManifest()
        {
            var log = new MemoryOperationLog();
            var dialogs = new FakeDialogs { TypeOnConfirm = "user@example.test" };
            var session = new TestSession(new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("e1", Utc(12, 9), Utc(12, 10), subject: "Одно"),
                InMemoryEvent.Single("e2", Utc(13, 9), Utc(13, 10), subject: "Другое"),
            }));
            var vm = new CleanupViewModel(dialogs, () => new DateTime(2026, 1, 20), log: log);
            vm.SetSession(session);
            vm.IsPeriod = true;
            vm.PeriodStart = new DateTime(2026, 1, 10);
            vm.PeriodEnd = new DateTime(2026, 1, 19);
            await vm.PreviewCommand.ExecuteAsync();

            await vm.DeleteCommand.ExecuteAsync();

            OperationRecord record = Assert.Single(log.Recent(10));
            Assert.Equal(OperationKind.Cleanup, record.Kind);
            Assert.Equal(2, record.Deleted);
            Assert.Null(record.Source);
            Assert.Equal("10.01.2026 – 19.01.2026", record.Period);
            Assert.Contains(record.Details, d => d.Contains("memory://manifest.csv"));
        }

        [Fact]
        public async Task Cleanup_ServerRefusesOne_RestDeleted_ShownAsNotAll_RefusalCountedInJournal()
        {
            var log = new MemoryOperationLog();
            var dialogs = new FakeDialogs { TypeOnConfirm = "user@example.test" };
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("locked", Utc(12, 9), Utc(12, 10), subject: "Чужое"),
                InMemoryEvent.Single("e2", Utc(13, 9), Utc(13, 10), subject: "Другое"),
            });
            var session = new TestSession(calendar, deleter: new RefusingDeleter(calendar, "locked"));
            var vm = new CleanupViewModel(dialogs, () => new DateTime(2026, 1, 20), log: log);
            vm.SetSession(session);
            vm.IsPeriod = true;
            vm.PeriodStart = new DateTime(2026, 1, 10);
            vm.PeriodEnd = new DateTime(2026, 1, 19);
            await vm.PreviewCommand.ExecuteAsync();

            await vm.DeleteCommand.ExecuteAsync();

            Assert.Equal("Готово, но не всё", vm.ResultTitle);
            Assert.Contains("ErrorAccessDenied", vm.ResultText);
            Assert.Null(vm.Error);
            OperationRecord record = Assert.Single(log.Recent(10));
            Assert.Equal(OperationOutcome.Problem, record.Outcome);
            Assert.Equal(1, record.Deleted);
            Assert.Equal(1, record.Failed);
            Assert.Equal(new[] { "locked" }, calendar.ItemIds());
        }

        /// <summary>Сервер отказывает в удалении одного элемента (нет прав на него).</summary>
        private sealed class RefusingDeleter : IwoBridge.Core.Abstractions.ITargetDeleter
        {
            private readonly IwoBridge.Core.Abstractions.ITargetDeleter _inner;
            private readonly string _locked;

            public RefusingDeleter(IwoBridge.Core.Abstractions.ITargetDeleter inner, string locked)
            {
                _inner = inner;
                _locked = locked;
            }

            public async Task<System.Collections.Generic.IReadOnlyList<IwoBridge.Core.Paging.DeleteResult>> DeleteAsync(
                System.Collections.Generic.IReadOnlyList<string> itemIds, IwoBridge.Core.Model.TargetDeleteMode mode, System.Threading.CancellationToken cancellationToken)
            {
                var allowed = itemIds.Where(id => id != _locked).ToList();
                var done = await _inner.DeleteAsync(allowed, mode, cancellationToken);
                int next = 0;
                return itemIds.Select(id => id == _locked ? IwoBridge.Core.Paging.DeleteResult.Failed("ErrorAccessDenied") : done[next++]).ToList();
            }
        }

        [Fact]
        public void HistoryWindowModel_ShowsNewestFirst_AddsLive_UnsubscribesOnClose()
        {
            var log = new MemoryOperationLog();
            log.Record(Record(OperationKind.Copy, 1, created: 3));
            var history = new HistoryViewModel(log, new FakeDialogs());
            Assert.Single(history.Rows);
            Assert.Contains("создано 3", history.Summary);
            Assert.StartsWith("С ", history.Summary, StringComparison.Ordinal); // журнал моложе недели — итог с первой записи

            log.Record(Record(OperationKind.Cleanup, 2, deleted: 5, outcome: OperationOutcome.Problem));
            Assert.Equal(2, history.Rows.Count);
            Assert.Equal("Очистка", history.Rows[0].Operation);
            Assert.True(history.Rows[0].IsProblem);
            Assert.Contains("удалено 5", history.Summary);

            history.Dispose();
            log.Record(Record(OperationKind.AutoCopy, 3));
            Assert.Equal(2, history.Rows.Count);
            Assert.False(history.ShowFileCommand.CanExecute(null)); // журнал в памяти — файла нет
        }

        [Fact]
        public void HistoryWindowModel_ShowsAtMostMaxRows_SummaryCountsWholeWeek()
        {
            // Автокопирование раз в 5 минут даёт за неделю больше двух тысяч записей: итог — по всем, строк — не больше MaxRows.
            var log = new MemoryOperationLog();
            log.Record(Record(OperationKind.Copy, 60 * 24 * 8, created: 100)); // старше недели — не в итоге
            int count = HistoryViewModel.MaxRows + 5;
            for (int i = count; i >= 1; i--)
            {
                log.Record(Record(OperationKind.AutoCopy, i, created: 1));
            }

            var history = new HistoryViewModel(log, new FakeDialogs());

            Assert.Equal(HistoryViewModel.MaxRows, history.Rows.Count);
            Assert.StartsWith("За 7 дней: операций " + RussianText.Number(count) + ",", history.Summary, StringComparison.Ordinal);
            Assert.Contains("создано " + RussianText.Number(count) + ",", history.Summary);
            Assert.True(history.Rows[0].Record.StartedUtc > history.Rows[1].Record.StartedUtc, "новые — сверху");
        }

        [Fact]
        public void HistoryWindow_DefaultSize_ColumnsFit_DetailsOfNewest_NoBindingErrors()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var log = new MemoryOperationLog();
                    log.Record(new OperationRecord(
                        OperationKind.AutoCopy, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-29), OperationOutcome.Problem,
                        "Outlook: Календарь — ящик", "box@example.test на mail.example.test", "25.09.2026 – 25.01.2027", 3, 1, 0, 1,
                        "Автокопирование в 14:30: скопировано с ошибками", new[] { "Не удалось: «Совещание» — сервер занят." }));
                    log.Record(Record(OperationKind.Cleanup, 1, deleted: 12));
                    var window = new HistoryWindow(new HistoryViewModel(log, new FakeDialogs()));
                    try
                    {
                        Visuals.ShowOffscreen(window, double.NaN, double.NaN);
                        await Visuals.Settle(window);

                        var grid = Visuals.ByAutomationId<DataGrid>(window, "HistoryRows");
                        ScrollViewer scroll = Visuals.All<ScrollViewer>(grid).First();
                        Assert.True(scroll.ScrollableWidth < 0.5, "колонки не помещаются: прокрутка вбок " + scroll.ScrollableWidth);
                        Assert.Equal(2, grid.Items.Count);

                        var details = Visuals.ByAutomationId<TextBox>(window, "HistoryDetails");
                        Assert.StartsWith("Очистка", details.Text, StringComparison.Ordinal);

                        string[] keys = Visuals.All<AccessText>(window).Where(t => t.IsVisible && t.AccessKey != default(char))
                            .Select(t => char.ToLowerInvariant(t.AccessKey).ToString()).ToArray();
                        Assert.Equal(keys.Length, keys.Distinct().Count());
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void MainWindow_HistoryCommand_OpensWithLog_DisabledWithout()
        {
            var dialogs = new FakeDialogs();
            var withLog = new MainViewModel(new DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs, log: new MemoryOperationLog());
            var withoutLog = new MainViewModel(new DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs);

            withLog.HistoryCommand.Execute(null);

            Assert.NotNull(dialogs.HistoryShown);
            Assert.False(withoutLog.HistoryCommand.CanExecute(null));
        }

        private static OperationRecord Record(OperationKind kind, int minutesAgo, int created = 0, int deleted = 0, OperationOutcome outcome = OperationOutcome.Success) =>
            new OperationRecord(
                kind, DateTime.UtcNow.AddMinutes(-minutesAgo), DateTime.UtcNow.AddMinutes(-minutesAgo).AddSeconds(5), outcome,
                kind == OperationKind.Cleanup ? null : "Outlook: Календарь", "box@example.test на mail.example.test", "01.01.2026 – 31.01.2026",
                created, 0, deleted, 0, "Готово");
    }
}
