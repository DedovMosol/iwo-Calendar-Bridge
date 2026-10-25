using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Сценарий копирования на календарях в памяти: первая заливка, повтор без изменений, точечное обновление и
    /// удаление, выбор отдельных действий, личные события, вложения только для записываемого, сбой одного события,
    /// неустранимая ошибка, остановка, массовое удаление, неполное чтение источника.
    /// </summary>
    public sealed class CopyRunnerTests
    {
        private static readonly DateTime From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task FirstCopy_CreatesAll_SecondRun_NothingToDo()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 5), Series("s", -10) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);

            CopyResult result = await RunAll(runner);

            Assert.True(result.Succeeded);
            Assert.Equal(3, result.Created);
            Assert.Equal(new[] { "a", "b", "s" }, target.Copies.Select(c => c.Item.SourceId).OrderBy(x => x));

            CopyPreview again = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);
            Assert.True(again.Plan.IsEmpty);
            Assert.Equal(3, again.Plan.UnchangedCount);
        }

        [Fact]
        public async Task ChangedAndDeletedInSource_PointwiseUpdate()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 5), Event("c", 7) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            await RunAll(runner);
            string copyOfA = target.Copies.Single(c => c.Item.SourceId == "a").ItemId;

            source.Put(Event("a", 3, subject: "Перенесли зал"));
            source.Remove("b");
            CopyResult result = await RunAll(runner);

            Assert.Equal(1, result.Updated);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(0, result.Created);
            InMemoryCopy a = target.Copies.Single(c => c.Item.SourceId == "a");
            Assert.Equal(copyOfA, a.ItemId); // тот же элемент приёмника — перезапись, не новая копия
            Assert.Equal("Перенесли зал", a.Item.Subject);
            Assert.DoesNotContain(target.Copies, c => c.Item.SourceId == "b");
        }

        [Fact]
        public async Task OnlySelectedActions_Applied()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 5) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            CopyPreview preview = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);

            CopyResult result = await runner.RunAsync(preview, preview.Plan.Actions.Where(a => a.SourceId == "b").ToArray(), null, CancellationToken.None);

            Assert.Equal(1, result.Created);
            Assert.Equal("b", Assert.Single(target.Copies).Item.SourceId);
        }

        [Fact]
        public async Task OwnEventsOfTarget_NeverTouched()
        {
            // В приёмнике копий нет — значит, удалять нечего, даже при пустом источнике.
            var source = new InMemorySource("источник", Array.Empty<SourceItem>());
            var runner = new CopyRunner(source, new InMemoryCopyTarget());

            CopyPreview preview = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);

            Assert.True(preview.Plan.IsEmpty);
            Assert.True(preview.Plan.CanApply);
        }

        [Fact]
        public async Task PrivateEvent_CopiedAsBusyOnly()
        {
            var secret = new SourceItem(
                "p", "Врач", "Поликлиника", "Анализы", Day(4), Day(4).AddHours(1),
                sensitivity: ItemSensitivity.Private, attachments: new[] { new SyncAttachment("направление.pdf", 10) });
            var source = new InMemorySource("источник", new[] { secret });
            var target = new InMemoryCopyTarget();

            await RunAll(new CopyRunner(source, target));

            SourceItem copy = Assert.Single(target.Copies).Item;
            Assert.Equal(CopyRules.PrivateSubject, copy.Subject);
            Assert.Equal(string.Empty, copy.Location);
            Assert.Equal(string.Empty, copy.Body);
            Assert.Empty(copy.Attachments);
            Assert.Equal(0, source.AttachmentLoads);
        }

        [Fact]
        public async Task Attachments_LoadedOnlyForWrittenEvents()
        {
            var withFile = new SourceItem("f", "Отчёт", null, null, Day(2), Day(2).AddHours(1), attachments: new[] { new SyncAttachment("отчёт.docx", 3) });
            var source = new InMemorySource("источник", new[] { withFile });
            source.Put(withFile, new[] { new AttachmentContent("отчёт.docx", Encoding.UTF8.GetBytes("abc")) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);

            await RunAll(runner);
            await RunAll(runner); // без изменений — вложения не читаются повторно

            Assert.Equal(1, source.AttachmentLoads);
            Assert.Equal("отчёт.docx", Assert.Single(Assert.Single(target.Copies).Attachments).Name);
        }

        [Fact]
        public async Task OneEventFails_OthersCopied_FailureReported()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("bad", 4), Event("c", 5) });
            var target = new InMemoryCopyTarget { FailOn = i => i.SourceId == "bad" ? new InvalidOperationException("сервер отклонил") : null };

            CopyResult result = await RunAll(new CopyRunner(source, target));

            Assert.Equal(2, result.Created);
            CopyFailure failure = Assert.Single(result.Failures);
            Assert.Equal("bad", failure.Action.SourceId);
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task FatalError_StopsAtOnce()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 4), Event("c", 5) });
            var target = new InMemoryCopyTarget { FailOn = i => i.SourceId == "b" ? new UnauthorizedAccessException("вход") : null };
            var runner = new CopyRunner(source, target, ex => ex is UnauthorizedAccessException);

            CopyResult result = await RunAll(runner);

            Assert.IsType<UnauthorizedAccessException>(result.Fatal);
            Assert.Equal(1, result.Created); // «c» уже не пробовали
            Assert.Empty(result.Failures);
        }

        [Fact]
        public async Task ConnectionErrorOnOneEvent_OthersStillCopied()
        {
            // Тайм-аут на одном событии (например, крупное вложение) не останавливает остальные: иначе это событие,
            // стоящее первым, навсегда останавливало бы автокопирование.
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("big", 4), Event("c", 5) });
            var target = new InMemoryCopyTarget { FailOn = i => i.SourceId == "big" ? new TimeoutException("тайм-аут") : null };
            var runner = new CopyRunner(source, target, ex => ex is TimeoutException || ex is UnauthorizedAccessException, stopsAtOnce: ex => ex is UnauthorizedAccessException);

            CopyResult result = await RunAll(runner);

            Assert.Null(result.Fatal);
            Assert.Equal(2, result.Created);
            Assert.Equal("big", Assert.Single(result.Failures).Action.SourceId);
        }

        [Fact]
        public async Task ConnectionErrorTwiceInARow_Stops()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 4), Event("c", 5), Event("d", 6) });
            var target = new InMemoryCopyTarget { FailOn = i => i.SourceId == "b" || i.SourceId == "c" ? new TimeoutException("нет связи") : null };
            var runner = new CopyRunner(source, target, ex => ex is TimeoutException, stopsAtOnce: _ => false);

            CopyResult result = await RunAll(runner);

            Assert.IsType<TimeoutException>(result.Fatal);
            Assert.Equal(1, result.Created); // «d» уже не пробовали
            Assert.Equal("b", Assert.Single(result.Failures).Action.SourceId);
        }

        [Fact]
        public async Task Recreate_CreateFails_OldCopyKept()
        {
            // Событие стало серией: новая копия не записалась — прежняя остаётся, событие не теряется.
            var source = new InMemorySource("источник", new[] { Event("a", 3) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            await RunAll(runner);
            source.Put(new SourceItem(
                "a", "Событие a", "Зал", "Повестка", Day(3), Day(3).AddHours(1),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Day(3).Date, days: WeekDays.Monday)));
            target.FailOn = _ => new InvalidOperationException("сервер отклонил");

            CopyResult result = await RunAll(runner);

            Assert.Single(result.Failures);
            Assert.Equal(SyncActionKind.Recreate, result.Failures[0].Action.Kind);
            Assert.Equal("a", Assert.Single(target.Copies).Item.SourceId);
        }

        [Fact]
        public async Task WrittenWithRemark_CountedAsDone_RemarkWithSubject_NotRepeatedNextRun()
        {
            // Приёмник записал копию, но перенёс не всё (изменённого повторения нет в серии копии): копия актуальна,
            // замечание — в итоге один раз, следующий запуск её не трогает (иначе серия пересоздавалась бы каждый раз).
            var source = new InMemorySource("источник", new[] { Series("s", -10), Event("a", 3) });
            var target = new InMemoryCopyTarget
            {
                RemarkOn = item => item.SourceId == "s" ? "изменение повторения от 05.10.2026 09:00 не перенесено" : null,
            };
            var runner = new CopyRunner(source, target);

            CopyResult first = await RunAll(runner);
            CopyResult second = await RunAll(runner);

            Assert.True(first.Succeeded);
            Assert.Equal(2, first.Created);
            string remark = Assert.Single(first.Remarks);
            Assert.StartsWith("«Планёрка»: изменение повторения", remark, StringComparison.Ordinal);
            Assert.Equal(0, second.Done);
            Assert.Empty(second.Remarks);
        }

        [Fact]
        public async Task Recreate_DeleteOfOldCopyFails_NewCopyCountedAsUpdated_FailureReported()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, new FailingDelete(target));
            await RunAll(runner);
            source.Put(new SourceItem(
                "a", "Событие a", "Зал", "Повестка", Day(3), Day(3).AddHours(1),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Day(3).Date, days: WeekDays.Monday)));

            CopyResult result = await RunAll(runner);

            // Новая копия записана — это перезапись; прежняя не удалилась — это сбой (дубль уберёт следующий запуск).
            Assert.Equal(1, result.Updated);
            Assert.Equal(SyncActionKind.Recreate, Assert.Single(result.Failures).Action.Kind);
            Assert.Equal(2, target.Copies.Count);
        }

        /// <summary>Приёмник, который не удаляет копии (сервер отказал).</summary>
        private sealed class FailingDelete : ICopyTarget
        {
            private readonly InMemoryCopyTarget _inner;

            public FailingDelete(InMemoryCopyTarget inner) => _inner = inner;

            public Task<System.Collections.Generic.IReadOnlyList<BridgeCopy>> ReadCopiesAsync(CancellationToken cancellationToken) =>
                _inner.ReadCopiesAsync(cancellationToken);

            public Task<System.Collections.Generic.IReadOnlyList<OwnEvent>> ReadOwnEventsAsync(DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken) =>
                _inner.ReadOwnEventsAsync(windowStartUtc, windowEndUtc, cancellationToken);

            public Task<System.Collections.Generic.IReadOnlyList<string>> CreateAsync(
                SourceItem item, System.Collections.Generic.IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken) =>
                _inner.CreateAsync(item, attachments, cancellationToken);

            public Task<System.Collections.Generic.IReadOnlyList<string>> UpdateAsync(
                string copyItemId, SourceItem item, System.Collections.Generic.IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken) =>
                _inner.UpdateAsync(copyItemId, item, attachments, cancellationToken);

            public Task DeleteAsync(string copyItemId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("сервер не удалил элемент");
        }

        [Fact]
        public async Task Stop_BeforeNextEvent_ExactCounts()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 4), Event("c", 5) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            CopyPreview preview = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);
            using (var stop = new CancellationTokenSource())
            {
                target.FailOn = i =>
                {
                    stop.Cancel(); // «Остановить» нажали во время записи первого события
                    return null;
                };

                CopyResult result = await runner.RunAsync(preview, preview.Plan.Actions, null, stop.Token);

                Assert.True(result.Cancelled);
                Assert.Equal(1, result.Created);
                Assert.Single(target.Copies);
            }
        }

        [Fact]
        public async Task EmptySourceWithCopies_RefusedUnlessConfirmed()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            await RunAll(runner);
            source.Remove("a");

            CopyPreview blocked = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);
            CopyResult refused = await runner.RunAsync(blocked, blocked.Plan.Actions, null, CancellationToken.None);
            CopyPreview confirmed = await runner.PreviewAsync(From, To, true, null, CancellationToken.None);
            CopyResult done = await runner.RunAsync(confirmed, confirmed.Plan.Actions, null, CancellationToken.None);

            Assert.NotNull(refused.RefusedReason);
            Assert.Equal(1, done.Deleted);
            Assert.Empty(target.Copies);
        }

        [Fact]
        public async Task IncompleteSource_NoDeletes()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3), Event("b", 4) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            await RunAll(runner);
            source.Remove("a");
            source.IncompleteReason = "обрыв связи";

            CopyPreview preview = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);

            Assert.DoesNotContain(preview.Plan.Actions, a => a.Kind == SyncActionKind.Delete);
            Assert.Equal("обрыв связи", preview.Plan.DeletesSuppressedReason);
        }

        [Fact]
        public async Task EventMovedOutOfPeriod_CopyFollows()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3) });
            var target = new InMemoryCopyTarget();
            var runner = new CopyRunner(source, target);
            await RunAll(runner);

            source.Put(Event("a", 45)); // перенесли на следующий месяц
            CopyResult result = await RunAll(runner);

            Assert.Equal(1, result.Updated);
            Assert.Equal(0, result.Deleted);
            Assert.Equal(Day(45), Assert.Single(target.Copies).Item.StartUtc);
        }

        [Fact]
        public async Task Progress_ReportsStages()
        {
            var source = new InMemorySource("источник", new[] { Event("a", 3) });
            var runner = new CopyRunner(source, new InMemoryCopyTarget());
            var stages = new System.Collections.Generic.List<CopyStage>();
            var progress = new SyncProgress<CopyProgress>(p => stages.Add(p.Stage));

            CopyPreview preview = await runner.PreviewAsync(From, To, false, progress, CancellationToken.None);
            await runner.RunAsync(preview, preview.Plan.Actions, progress, CancellationToken.None);

            Assert.Equal(new[] { CopyStage.ReadingTarget, CopyStage.ReadingSource, CopyStage.Writing }, stages.Distinct());
        }

        [Fact]
        public async Task BadPeriod_Rejected()
        {
            var runner = new CopyRunner(new InMemorySource("и", Array.Empty<SourceItem>()), new InMemoryCopyTarget());

            await Assert.ThrowsAsync<ArgumentException>(() => runner.PreviewAsync(To, From, false, null, CancellationToken.None));
        }

        private static async Task<CopyResult> RunAll(CopyRunner runner)
        {
            CopyPreview preview = await runner.PreviewAsync(From, To, false, null, CancellationToken.None);
            return await runner.RunAsync(preview, preview.Plan.Actions, null, CancellationToken.None);
        }

        private static DateTime Day(int day) => From.AddDays(day).AddHours(9);

        private static SourceItem Event(string id, int day, string? subject = null) =>
            new SourceItem(id, subject ?? "Событие " + id, "Зал", "Повестка", Day(day), Day(day).AddHours(1));

        private static SourceItem Series(string id, int day) =>
            new SourceItem(
                id, "Планёрка", null, null, Day(day), Day(day).AddHours(1),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Day(day).Date, days: WeekDays.Monday));

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            public SyncProgress(Action<T> report) => _report = report;

            public void Report(T value) => _report(value);
        }
    }
}
