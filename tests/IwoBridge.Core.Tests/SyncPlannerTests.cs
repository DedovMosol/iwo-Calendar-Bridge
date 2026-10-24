using System;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Точечная синхронизация за период: создаётся новое, перезаписывается изменённое, удаляются копии удалённого,
    /// не трогается неизменённое и чужое. Защиты: неполный снимок не удаляет, массовое удаление не применяется
    /// без подтверждения, повторяющийся идентификатор источника — без действий.
    /// </summary>
    public sealed class SyncPlannerTests
    {
        private static readonly DateTime From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void NewEventInPeriod_Created()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5)) });

            SyncAction action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionKind.Create, action.Kind);
            Assert.Equal(SyncReason.NewInSource, action.Reason);
            Assert.True(plan.CanApply);
        }

        [Fact]
        public void SameHash_Untouched()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h1") }, Copy("t1", "a", Day(5), "h1"));

            Assert.True(plan.IsEmpty);
            Assert.Equal(1, plan.UnchangedCount);
        }

        [Fact]
        public void ChangedHash_UpdatedInPlace()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h2") }, Copy("t1", "a", Day(5), "h1"));

            SyncAction action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionKind.Update, action.Kind);
            Assert.Equal(SyncReason.ChangedInSource, action.Reason);
            Assert.Equal("t1", action.Copy!.ItemId);
        }

        [Fact]
        public void CopyWithoutHash_UpdatedOnce()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h1") }, Copy("t1", "a", Day(5), null));

            Assert.Equal(SyncReason.UnknownCopyVersion, Assert.Single(plan.Actions).Reason);
        }

        [Fact]
        public void BecameSeries_Recreated()
        {
            SyncPlan plan = Plan(
                new[] { Source("a", Day(5), "h2", ItemKind.RecurringMaster) },
                Copy("t1", "a", Day(5), "h1"));

            SyncAction action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionKind.Recreate, action.Kind);
            Assert.Equal(SyncReason.KindChanged, action.Reason);
        }

        [Fact]
        public void DeletedInSource_CopyInPeriodDeleted()
        {
            SyncPlan plan = Plan(new[] { Source("b", Day(6)) }, Copy("t1", "a", Day(5), "h1"), Copy("t2", "b", Day(6), "h"));

            SyncAction action = plan.Actions.Single(a => a.Kind == SyncActionKind.Delete);
            Assert.Equal(SyncReason.DeletedInSource, action.Reason);
            Assert.Equal("t1", action.Copy!.ItemId);
            Assert.Equal(1, plan.UnchangedCount);
        }

        [Fact]
        public void OrphanCopyOutsidePeriod_NotTouched()
        {
            // О копиях вне периода снимок периода ничего не знает — удалять их по нему нельзя.
            SyncPlan plan = Plan(new[] { Source("b", Day(6)) }, Copy("t1", "a", Day(-20), "h1"), Copy("t2", "b", Day(6), "h"));

            Assert.True(plan.IsEmpty);
        }

        [Fact]
        public void EventMovedOutOfPeriod_CopyFollows_NotDeleted()
        {
            // Снимок содержит событие вне периода, потому что на него ссылается копия периода (контракт снимка).
            SyncPlan plan = Plan(new[] { Source("a", Day(45), "h2") }, Copy("t1", "a", Day(5), "h1"));

            SyncAction action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionKind.Update, action.Kind);
            Assert.Equal("t1", action.Copy!.ItemId);
        }

        [Fact]
        public void EventMovedIntoPeriod_ExistingCopyUpdated_NoDuplicate()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h2") }, Copy("t1", "a", Day(-10), "h1"));

            SyncAction action = Assert.Single(plan.Actions);
            Assert.Equal(SyncActionKind.Update, action.Kind);
        }

        [Fact]
        public void SourceOutsidePeriodWithoutCopy_Ignored()
        {
            Assert.True(Plan(new[] { Source("a", Day(45)) }).IsEmpty);
        }

        [Fact]
        public void DuplicateCopies_OneKept_PreferringCurrentHash()
        {
            SyncPlan plan = Plan(
                new[] { Source("a", Day(5), "h2") },
                Copy("t1", "a", Day(5), "h1"),
                Copy("t2", "a", Day(5), "h2"),
                Copy("t3", "a", Day(5), null));

            Assert.Equal(new[] { "t1", "t3" }, plan.Actions.Where(a => a.Reason == SyncReason.DuplicateCopy).Select(a => a.Copy!.ItemId).OrderBy(x => x));
            Assert.Equal(2, plan.Actions.Count);
            Assert.Equal(1, plan.UnchangedCount);
        }

        [Fact]
        public void DuplicateCopies_NoneCurrent_FirstByIdKeptAndUpdated()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h3") }, Copy("t2", "a", Day(5), "h1"), Copy("t1", "a", Day(5), "h2"));

            Assert.Equal("t1", plan.Actions.Single(a => a.Kind == SyncActionKind.Update).Copy!.ItemId);
            Assert.Equal("t2", plan.Actions.Single(a => a.Kind == SyncActionKind.Delete).Copy!.ItemId);
        }

        [Fact]
        public void IncompleteSource_NoDeletes_CreatesAndUpdatesKept()
        {
            var source = new SourceSnapshot(new[] { Source("a", Day(5), "h2"), Source("n", Day(7)) }, isComplete: false, "обрыв связи");

            SyncPlan plan = SyncPlanner.Plan(From, To, source, new[] { Copy("t1", "a", Day(5), "h1"), Copy("t9", "gone", Day(8), "h") });

            Assert.DoesNotContain(plan.Actions, a => a.Kind == SyncActionKind.Delete);
            Assert.Equal(1, plan.Count(SyncActionKind.Update));
            Assert.Equal(1, plan.Count(SyncActionKind.Create));
            Assert.Equal("обрыв связи", plan.DeletesSuppressedReason);
            Assert.True(plan.CanApply);
        }

        [Fact]
        public void EmptySource_WithCopies_Blocked()
        {
            SyncPlan plan = Plan(Array.Empty<SourceEvent>(), Copy("t1", "a", Day(5), "h"));

            Assert.False(plan.CanApply);
            Assert.Contains("пуст", plan.BlockReason);
            Assert.Equal(1, plan.Count(SyncActionKind.Delete)); // план виден для показа
        }

        [Fact]
        public void MassDelete_Blocked_UnlessConfirmed()
        {
            // 25 удалений при 1 000 копий: доля мала (2,5 %), но больше 20 за прогон.
            SourceEvent[] kept = Enumerable.Range(0, 975).Select(i => Source("k" + i, Day(1).AddMinutes(i))).ToArray();
            BridgeCopy[] copies = kept.Select((s, i) => Copy("tk" + i, s.SourceId, s.SpanStartUtc, s.ContentHash))
                .Concat(Enumerable.Range(0, 25).Select(i => Copy("tg" + i, "gone" + i, Day(2).AddHours(i), "h")))
                .ToArray();

            SyncPlan blocked = SyncPlanner.Plan(From, To, new SourceSnapshot(kept, true), copies);
            SyncPlan confirmed = SyncPlanner.Plan(From, To, new SourceSnapshot(kept, true), copies, new SyncOptions(allowMassDelete: true));

            Assert.False(blocked.CanApply);
            Assert.Contains("25", blocked.BlockReason);
            Assert.True(confirmed.CanApply);
            Assert.Equal(25, confirmed.Count(SyncActionKind.Delete));
        }

        [Theory]
        [InlineData(30, 6, false)] // 20 % копий периода — подозрительно
        [InlineData(100, 6, true)] // 6 % — обычная правка
        [InlineData(30, 5, true)] // не больше порога доли — не проверяется
        public void DeleteShare_CheckedAboveFloor(int copies, int deleted, bool canApply)
        {
            SourceEvent[] kept = Enumerable.Range(0, copies - deleted).Select(i => Source("k" + i, Day(1).AddMinutes(i))).ToArray();
            BridgeCopy[] all = kept.Select((s, i) => Copy("tk" + i, s.SourceId, s.SpanStartUtc, s.ContentHash))
                .Concat(Enumerable.Range(0, deleted).Select(i => Copy("tg" + i, "gone" + i, Day(2).AddHours(i), "h")))
                .ToArray();

            SyncPlan plan = SyncPlanner.Plan(From, To, new SourceSnapshot(kept, true), all);

            Assert.Equal(canApply, plan.CanApply);
            Assert.Equal(deleted, plan.Count(SyncActionKind.Delete));
        }

        [Fact]
        public void FewDeletes_BelowMinimum_NotBlocked()
        {
            SyncPlan plan = Plan(new[] { Source("k", Day(1)) }, Copy("tk", "k", Day(1), "h"), Copy("t1", "x", Day(2), "h"), Copy("t2", "y", Day(3), "h"));

            Assert.True(plan.CanApply);
            Assert.Equal(2, plan.Count(SyncActionKind.Delete));
        }

        [Fact]
        public void DuplicateSourceId_NoActions_Reported()
        {
            SyncPlan plan = Plan(new[] { Source("a", Day(5), "h1"), Source("a", Day(6), "h2") }, Copy("t1", "a", Day(5), "h0"));

            Assert.True(plan.IsEmpty);
            Assert.Equal(new[] { "a" }, plan.ConflictingSourceIds);
        }

        [Fact]
        public void SeriesSpanningPeriod_ComparedAsUnit()
        {
            var series = new SourceEvent("s", ItemKind.RecurringMaster, Day(-60), DateTime.MaxValue, "h2");
            var copy = new BridgeCopy("ts", "s", ItemKind.RecurringMaster, Day(-60), DateTime.MaxValue, "h1");

            SyncAction action = Assert.Single(SyncPlanner.Plan(From, To, new SourceSnapshot(new[] { series }, true), new[] { copy }).Actions);
            Assert.Equal(SyncActionKind.Update, action.Kind);
        }

        [Fact]
        public void DeletedSeriesOverlappingPeriod_CopyDeleted()
        {
            var copy = new BridgeCopy("ts", "s", ItemKind.RecurringMaster, Day(-60), DateTime.MaxValue, "h1");

            SyncPlan plan = Plan(new[] { Source("k", Day(1)) }, copy, Copy("tk", "k", Day(1), "h"));

            Assert.Equal("ts", plan.Actions.Single(a => a.Reason == SyncReason.DeletedInSource).Copy!.ItemId);
        }

        [Fact]
        public void ActionsOrdered_DeletesThenRecreatesThenUpdatesThenCreates()
        {
            SyncPlan plan = Plan(
                new[]
                {
                    Source("new", Day(1)),
                    Source("chg", Day(2), "h2"),
                    Source("kind", Day(3), "h2", ItemKind.RecurringMaster),
                },
                Copy("t-chg", "chg", Day(2), "h1"),
                Copy("t-kind", "kind", Day(3), "h1"),
                Copy("t-gone", "gone", Day(4), "h"));

            Assert.Equal(
                new[] { SyncActionKind.Delete, SyncActionKind.Recreate, SyncActionKind.Update, SyncActionKind.Create },
                plan.Actions.Select(a => a.Kind));
        }

        [Fact]
        public void Deterministic_SameInputSamePlan()
        {
            SourceEvent[] sources = { Source("b", Day(2)), Source("a", Day(2)), Source("c", Day(1), "h9") };
            BridgeCopy[] copies = { Copy("t2", "c", Day(1), "h1"), Copy("t1", "x", Day(3), "h") };

            string first = Describe(SyncPlanner.Plan(From, To, new SourceSnapshot(sources, true), copies));
            string second = Describe(SyncPlanner.Plan(From, To, new SourceSnapshot(sources.Reverse(), true), copies.Reverse()));

            Assert.Equal(first, second);
        }

        [Fact]
        public void BadArguments_Rejected()
        {
            Assert.Throws<ArgumentException>(() => SyncPlanner.Plan(To, From, new SourceSnapshot(Array.Empty<SourceEvent>(), true), Array.Empty<BridgeCopy>()));
            Assert.Throws<ArgumentException>(() => new SourceEvent("", ItemKind.Single, Day(1), Day(1), "h"));
            Assert.Throws<ArgumentException>(() => new SourceEvent("a", ItemKind.Single, Day(1), Day(1), ""));
            Assert.Throws<ArgumentException>(() => new BridgeCopy("t", "", ItemKind.Single, Day(1), Day(1), "h"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions(maxDeleteShare: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions(maxDeleteShare: 1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions(maxDeletes: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions(shareFloor: -1));
        }

        private static string Describe(SyncPlan plan) =>
            string.Join("|", plan.Actions.Select(a => a.Kind + ":" + a.SourceId + ":" + (a.Copy?.ItemId ?? "-")));

        private static SyncPlan Plan(SourceEvent[] sources, params BridgeCopy[] copies) =>
            SyncPlanner.Plan(From, To, new SourceSnapshot(sources, isComplete: true), copies);

        private static DateTime Day(int day) => From.AddDays(day).AddHours(9);

        private static SourceEvent Source(string id, DateTime start, string hash = "h", ItemKind kind = ItemKind.Single) =>
            new SourceEvent(id, kind, start, start.AddHours(1), hash);

        private static BridgeCopy Copy(string itemId, string sourceId, DateTime start, string? hash) =>
            new BridgeCopy(itemId, sourceId, ItemKind.Single, start, start.AddHours(1), hash);
    }
}
