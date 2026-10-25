using System;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;
using Xunit;
using static IwoBridge.Core.Tests.TestData;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// События «на весь день» и период очистки: сутки события в поясе организатора могут сдвинуться на несколько часов
    /// относительно границ периода (летнее время, неточное смещение). Соседний день задевает период на эти часы — и не
    /// должен удаляться; сам день события — должен.
    /// </summary>
    public sealed class AllDayCleanupTests
    {
        // Период — 10 июня в поясе +03:00: [09.06 21:00Z, 10.06 21:00Z).
        private static readonly CleanupScope June10 = CleanupScope.Period(Utc(2026, 6, 9, 21), Utc(2026, 6, 10, 21));

        private static TargetItemInfo AllDay(string id, DateTime startUtc, int days = 1) =>
            new TargetItemInfo(id, "CK-" + id, "IPM.Appointment", ItemKind.Single, startUtc, startUtc.AddDays(days), null, null, null, isAllDay: true);

        [Fact]
        public void NeighbourDayInOtherZone_TouchingPeriodByHour_NotDeleted()
        {
            // 9 июня в поясе +02:00 (летнее время организатора): [08.06 22:00Z, 09.06 22:00Z) — задевает период на час.
            CleanupPlan plan = CleanupPlanner.BuildPlan(new[] { AllDay("a", Utc(2026, 6, 8, 22)) }, June10, CleanupFilter.AllEvents, Array.Empty<TargetOccurrence>());

            Assert.Empty(plan.ToDelete);
        }

        [Fact]
        public void SameDayInOtherZone_Deleted()
        {
            // 10 июня в поясе +02:00: [09.06 22:00Z, 10.06 22:00Z).
            CleanupPlan plan = CleanupPlanner.BuildPlan(new[] { AllDay("a", Utc(2026, 6, 9, 22)) }, June10, CleanupFilter.AllEvents, Array.Empty<TargetOccurrence>());

            Assert.Single(plan.ToDelete);
        }

        [Fact]
        public void MultiDayCoveringPeriodDay_Deleted()
        {
            // 8–11 июня в поясе +03:00 — включает 10 июня.
            CleanupPlan plan = CleanupPlanner.BuildPlan(new[] { AllDay("a", Utc(2026, 6, 7, 21), days: 4) }, June10, CleanupFilter.AllEvents, Array.Empty<TargetOccurrence>());

            Assert.Single(plan.ToDelete);
        }

        [Fact]
        public void TimedEvent_SameHourOverlap_StillDeleted()
        {
            // Обычное событие правило не меняет: пересекает период хотя бы частично — удаляется.
            TargetItemInfo timed = Single("t", Utc(2026, 6, 9, 20), Utc(2026, 6, 9, 22));
            CleanupPlan plan = CleanupPlanner.BuildPlan(new[] { timed }, June10, CleanupFilter.AllEvents, Array.Empty<TargetOccurrence>());

            Assert.Single(plan.ToDelete);
        }

        [Fact]
        public void AllDaySeries_NeighbourOccurrenceNotTrimmed_OwnDayTrimmed()
        {
            var master = new TargetItemInfo(
                "m", "CK-m", "IPM.Appointment", ItemKind.RecurringMaster, Utc(2026, 6, 1, 22), Utc(2026, 6, 2, 22),
                Utc(2026, 6, 1, 22), Utc(2026, 6, 30, 22), null, "U1", isAllDay: true);
            var neighbour = new TargetOccurrence("o9", "U1", Utc(2026, 6, 8, 22), Utc(2026, 6, 9, 22), true, isAllDay: true);
            var own = new TargetOccurrence("o10", "U1", Utc(2026, 6, 9, 22), Utc(2026, 6, 10, 22), true, isAllDay: true);

            CleanupPlan plan = CleanupPlanner.BuildPlan(new[] { master }, June10, CleanupFilter.AllEvents, new[] { neighbour, own });

            Assert.Equal(new[] { "o10" }, System.Linq.Enumerable.Select(plan.OccurrencesToDelete, o => o.OccurrenceItemId));
        }

        [Fact]
        public void Overlaps_AllDay_SaturatesAtBounds()
        {
            Assert.True(TimeWindow.Overlaps(DateTime.MinValue, DateTime.MaxValue, true, Utc(2026, 1, 1), Utc(2026, 1, 2)));
            Assert.False(TimeWindow.Overlaps(DateTime.MaxValue, DateTime.MaxValue, true, Utc(2026, 1, 1), Utc(2026, 1, 2)));
        }
    }
}
