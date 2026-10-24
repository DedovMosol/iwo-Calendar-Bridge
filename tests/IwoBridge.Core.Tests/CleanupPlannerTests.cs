using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using Xunit;
using static IwoBridge.Core.Tests.TestData;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Семантика планирования очистки: что удаляется целиком, что обрезается, что пропускается и
    /// почему. Чистая функция — тесты без сети и без ин-мемори календаря.
    /// </summary>
    public sealed class CleanupPlannerTests
    {
        // Окно периода: [10 янв 00:00 UTC, 20 янв 00:00 UTC).
        private static CleanupScope Period() => CleanupScope.Period(Utc(2026, 1, 10), Utc(2026, 1, 20));

        private static CleanupPlan PlanPeriod(IReadOnlyCollection<TargetItemInfo> items, params TargetOccurrence[] occurrences)
        {
            return CleanupPlanner.BuildPlan(items, Period(), CleanupFilter.AllEvents, occurrences);
        }

        private static bool Contains(IEnumerable<TargetItemInfo> items, string id) => items.Any(i => i.ItemId == id);

        private static SkipReason ReasonFor(CleanupPlan plan, string id) => plan.Skipped.Single(s => s.Item.ItemId == id).Reason;

        [Fact]
        public void Period_SingleInside_Deleted()
        {
            CleanupPlan plan = PlanPeriod(new[] { Single("s1", Utc(2026, 1, 15, 9), Utc(2026, 1, 15, 10)) });

            Assert.True(Contains(plan.ToDelete, "s1"));
            Assert.Equal(1, plan.DeleteCount);
        }

        [Theory]
        [InlineData(5)]
        [InlineData(25)]
        public void Period_SingleOutside_Skipped(int day)
        {
            CleanupPlan plan = PlanPeriod(new[] { Single("s1", Utc(2026, 1, day, 9), Utc(2026, 1, day, 10)) });

            Assert.Equal(0, plan.DeleteCount);
            Assert.Equal(SkipReason.OutsidePeriod, ReasonFor(plan, "s1"));
        }

        [Fact]
        public void Period_SingleCrossingWindowStart_Deleted()
        {
            CleanupPlan plan = PlanPeriod(new[] { Single("s1", Utc(2026, 1, 9, 23), Utc(2026, 1, 10, 1)) });

            Assert.True(Contains(plan.ToDelete, "s1"));
        }

        [Fact]
        public void Period_SingleEndingExactlyAtWindowStart_Skipped()
        {
            CleanupPlan plan = PlanPeriod(new[] { Single("s1", Utc(2026, 1, 9, 23), Utc(2026, 1, 10)) });

            Assert.Equal(0, plan.DeleteCount);
            Assert.Equal(SkipReason.OutsidePeriod, ReasonFor(plan, "s1"));
        }

        [Fact]
        public void NonAppointmentClass_SkippedRegardlessOfTime()
        {
            CleanupPlan plan = PlanPeriod(new[] { Single("n1", Utc(2026, 1, 15), Utc(2026, 1, 15, 1), itemClass: "IPM.Note") });

            Assert.Equal(0, plan.DeleteCount);
            Assert.Equal(SkipReason.NonAppointmentClass, ReasonFor(plan, "n1"));
        }

        [Fact]
        public void Period_MasterFullyInside_DeletedWhole()
        {
            CleanupPlan plan = PlanPeriod(new[] { Master("m1", Utc(2026, 1, 12), Utc(2026, 1, 18), uid: "U1") });

            Assert.True(Contains(plan.ToDelete, "m1"));
            Assert.Equal(0, plan.TrimCount);
            Assert.Empty(plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_MasterSpanningBoundary_TrimsOnlyInstancesInWindow()
        {
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m2", Utc(2026, 1, 5), Utc(2026, 1, 15), uid: "U2") },
                Instance("o-05", "U2", Utc(2026, 1, 5)),
                Instance("o-12", "U2", Utc(2026, 1, 12)),
                Instance("o-15", "U2", Utc(2026, 1, 15)));

            Assert.True(Contains(plan.BoundarySeriesToTrim, "m2"));
            Assert.Equal(0, plan.DeleteCount);
            Assert.Equal(new[] { "o-12", "o-15" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_SeriesFirstStartsBeforeWindowEndsInside_DeletedWhole_NoEmptyMaster()
        {
            // Аудит L1: первое повторение 9 янв 23:30–10 янв 00:30 пересекает период — по единому правилу оно «в
            // периоде». Раньше серию обрезали, удаляли оба экземпляра и оставляли пустой мастер.
            var master = new TargetItemInfo(
                "m", "ck", "IPM.Appointment", ItemKind.RecurringMaster,
                Utc(2026, 1, 9, 23, 30), Utc(2026, 1, 10, 0, 30), Utc(2026, 1, 9, 23, 30), Utc(2026, 1, 15, 23, 30), null, "U");

            CleanupPlan plan = PlanPeriod(
                new[] { master },
                Instance("o-1", "U", Utc(2026, 1, 9, 23, 30), Utc(2026, 1, 10, 0, 30)),
                Instance("o-2", "U", Utc(2026, 1, 15, 23, 30)));

            Assert.True(Contains(plan.ToDelete, "m"));
            Assert.Empty(plan.OccurrencesToDelete);
        }

        [Fact]
        public void Period_SeriesSpanningWindowWithoutInstanceInside_SkippedAsOutside()
        {
            // Аудит L2: ежегодная серия (2025–2027) и период в 10 дней без её экземпляров — вне периода, а не «обрезка
            // пустоты».
            CleanupPlan plan = PlanPeriod(
                new[] { Master("yearly", Utc(2025, 6, 1), Utc(2027, 6, 1), uid: "Y") },
                Instance("y-2026", "Y", Utc(2026, 6, 1)));

            Assert.Equal(0, plan.TrimCount);
            Assert.Equal(SkipReason.OutsidePeriod, ReasonFor(plan, "yearly"));
        }

        [Fact]
        public void Period_TrimCandidateSharingUidWithWholeDeletedSeries_Skipped()
        {
            // Аудит: серия целиком в периоде и граничная серия с тем же UID — экземпляры не различить; обрезка дала бы
            // двойной учёт уже удалённых вместе с мастером экземпляров.
            CleanupPlan plan = PlanPeriod(
                new[]
                {
                    Master("inside", Utc(2026, 1, 12), Utc(2026, 1, 14), uid: "SAME"),
                    Master("boundary", Utc(2026, 1, 5), Utc(2026, 1, 25), uid: "SAME"),
                },
                Instance("i-12", "SAME", Utc(2026, 1, 12)),
                Instance("b-15", "SAME", Utc(2026, 1, 15)));

            Assert.True(Contains(plan.ToDelete, "inside"));
            Assert.Equal(SkipReason.AmbiguousSeriesUid, ReasonFor(plan, "boundary"));
            Assert.Empty(plan.OccurrencesToDelete);
        }

        [Fact]
        public void UnknownFilter_Throws_InsteadOfDeletingEverything()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CleanupPlanner.BuildPlan(
                new[] { Single("s", Utc(2026, 1, 15), Utc(2026, 1, 15, 1)) },
                CleanupScope.WholeCalendar(),
                (CleanupFilter)99,
                null));
        }

        [Fact]
        public void Period_TrimmedInstances_CarryTimeAndSubjectForManifest()
        {
            // Для манифеста план несёт сами экземпляры (время, тема), а не только их идентификаторы.
            var moved = new TargetOccurrence("o-12", "U2", Utc(2026, 1, 12, 9), Utc(2026, 1, 12, 11), true, "Перенесено");
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m2", Utc(2026, 1, 5), Utc(2026, 1, 25), uid: "U2") },
                Instance("o-05", "U2", Utc(2026, 1, 5)),
                moved);

            TargetOccurrence planned = Assert.Single(plan.OccurrencesToDelete);
            Assert.Same(moved, planned);
            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
            Assert.Equal(1, plan.OccurrenceDeleteCount);
        }

        [Fact]
        public void Period_SameInstanceTwiceInWindow_PlannedOnce()
        {
            // Дробление окна CalendarView может вернуть экземпляр дважды (на стыке отрезков) — удаляем один раз.
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m2", Utc(2026, 1, 5), Utc(2026, 1, 25), uid: "U2") },
                Instance("o-12", "U2", Utc(2026, 1, 12)),
                Instance("o-12", "U2", Utc(2026, 1, 12)));

            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
            Assert.Single(plan.OccurrencesToDelete);
        }

        [Fact]
        public void Period_MasterLastOccurrenceEndsInsideWindow_Trimmed()
        {
            // Последнее повторение начинается до окна (9 янв 23:30), но заканчивается в нём (10 янв 00:30).
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m6", Utc(2026, 1, 5), Utc(2026, 1, 9, 23, 30), uid: "U6") },
                Instance("o-last", "U6", Utc(2026, 1, 9, 23, 30)));

            Assert.True(Contains(plan.BoundarySeriesToTrim, "m6"));
            Assert.Equal(new[] { "o-last" }, plan.OccurrenceIdsToDelete);
            Assert.Empty(plan.Skipped);
        }

        [Fact]
        public void Period_MasterFullyOutside_Skipped()
        {
            CleanupPlan plan = PlanPeriod(new[] { Master("m3", Utc(2026, 1, 1), Utc(2026, 1, 8), uid: "U3") });

            Assert.Equal(SkipReason.OutsidePeriod, ReasonFor(plan, "m3"));
            Assert.Empty(plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_ExceptionMovedIntoWindow_SeriesTrimmed()
        {
            // По правилу серия вне периода, но исключение перенесено в период — его видно в раскрытом окне.
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m7", Utc(2026, 1, 1), Utc(2026, 1, 5), uid: "U7") },
                Instance("o-moved", "U7", Utc(2026, 1, 12)));

            Assert.True(Contains(plan.BoundarySeriesToTrim, "m7"));
            Assert.Equal(new[] { "o-moved" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_ExceptionMovedOutOfWindow_SeriesTrimmedNotDeletedWhole()
        {
            // По правилу серия целиком в периоде, но одно повторение перенесено на 25 янв — удалять серию
            // целиком нельзя, иначе исчезнет событие вне периода.
            var moved = new[] { new OccurrenceSpan(Utc(2026, 1, 25, 9), Utc(2026, 1, 25, 10)) };
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m8", Utc(2026, 1, 12), Utc(2026, 1, 16), uid: "U8", modified: moved) },
                Instance("o-12", "U8", Utc(2026, 1, 12)),
                Instance("o-14", "U8", Utc(2026, 1, 14)),
                Instance("o-16", "U8", Utc(2026, 1, 16)));

            Assert.Equal(0, plan.DeleteCount);
            Assert.True(Contains(plan.BoundarySeriesToTrim, "m8"));
            Assert.Equal(new[] { "o-12", "o-14", "o-16" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_OpenEndedMasterStartingBeforeEnd_Trimmed()
        {
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m4", Utc(2026, 1, 5), lastOccurrenceUtc: null, uid: "U4") },
                Instance("o-12", "U4", Utc(2026, 1, 12)));

            Assert.True(Contains(plan.BoundarySeriesToTrim, "m4"));
            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Period_OpenEndedMasterStartingAfterEnd_Skipped()
        {
            CleanupPlan plan = PlanPeriod(new[] { Master("m5", Utc(2026, 2, 1), lastOccurrenceUtc: null, uid: "U5") });

            Assert.Equal(SkipReason.OutsidePeriod, ReasonFor(plan, "m5"));
        }

        [Fact]
        public void Trim_InstanceEndingExactlyAtWindowStart_NotDeleted()
        {
            // CalendarView возвращает и экземпляр, закончившийся ровно в начале окна; в период он не входит.
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m9", Utc(2026, 1, 5), Utc(2026, 1, 15), uid: "U9") },
                Instance("o-edge", "U9", Utc(2026, 1, 9, 23), Utc(2026, 1, 10)),
                Instance("o-12", "U9", Utc(2026, 1, 12)));

            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Trim_SingleSharingSeriesUid_NotDeleted()
        {
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m10", Utc(2026, 1, 5), Utc(2026, 1, 15), uid: "U10") },
                Instance("o-12", "U10", Utc(2026, 1, 12)),
                SingleInWindow("s-13", "U10", Utc(2026, 1, 13)));

            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Trim_UidSharedWithPreservedForeignSeries_SkippedAsAmbiguous()
        {
            var items = new[]
            {
                Master("mb", Utc(2026, 1, 5), Utc(2026, 1, 15), bridgeSourceId: "src", uid: "U11"),
                Master("mf", Utc(2026, 1, 5), Utc(2026, 1, 15), uid: "U11"),
            };

            CleanupPlan plan = CleanupPlanner.BuildPlan(
                items, Period(), CleanupFilter.BridgeCopiesOnly, new[] { Instance("o-12", "U11", Utc(2026, 1, 12)) });

            Assert.Equal(SkipReason.NotBridgeCopy, ReasonFor(plan, "mf"));
            Assert.Equal(SkipReason.AmbiguousSeriesUid, ReasonFor(plan, "mb"));
            Assert.Equal(0, plan.TrimCount);
            Assert.Empty(plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Trim_MissingUid_SkippedWithReason()
        {
            CleanupPlan plan = PlanPeriod(new[] { Master("m12", Utc(2026, 1, 5), Utc(2026, 1, 15)) });

            Assert.Equal(SkipReason.SeriesUidUnavailable, ReasonFor(plan, "m12"));
            Assert.Empty(plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void Trim_DuplicateInstanceIds_ListedOnce()
        {
            CleanupPlan plan = PlanPeriod(
                new[] { Master("m13", Utc(2026, 1, 5), Utc(2026, 1, 15), uid: "U13") },
                Instance("o-12", "U13", Utc(2026, 1, 12)),
                Instance("o-12", "U13", Utc(2026, 1, 12)));

            Assert.Equal(new[] { "o-12" }, plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void WholeCalendar_DeletesAppointmentsAndSkipsOtherClasses()
        {
            var items = new[]
            {
                Single("s1", Utc(2026, 5, 1), Utc(2026, 5, 1, 1)),
                Master("m1", Utc(2000, 1, 1), null, uid: "U1"),
                Single("n1", Utc(2026, 5, 1), Utc(2026, 5, 1, 1), itemClass: "IPM.Note"),
            };

            CleanupPlan plan = CleanupPlanner.BuildPlan(items, CleanupScope.WholeCalendar(), CleanupFilter.AllEvents, null);

            Assert.Equal(2, plan.DeleteCount);
            Assert.True(Contains(plan.ToDelete, "s1"));
            Assert.True(Contains(plan.ToDelete, "m1"));
            Assert.Equal(SkipReason.NonAppointmentClass, ReasonFor(plan, "n1"));
            Assert.Empty(plan.OccurrenceIdsToDelete);
        }

        [Fact]
        public void BridgeCopiesOnly_SkipsForeignEvents()
        {
            var items = new[]
            {
                Single("mine", Utc(2026, 1, 15), Utc(2026, 1, 15, 1), bridgeSourceId: "src-1"),
                Single("foreign", Utc(2026, 1, 15), Utc(2026, 1, 15, 1)),
            };

            CleanupPlan plan = CleanupPlanner.BuildPlan(items, Period(), CleanupFilter.BridgeCopiesOnly, Array.Empty<TargetOccurrence>());

            Assert.True(Contains(plan.ToDelete, "mine"));
            Assert.Equal(SkipReason.NotBridgeCopy, ReasonFor(plan, "foreign"));
        }

        [Fact]
        public void Period_WithoutOccurrences_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => CleanupPlanner.BuildPlan(
                new[] { Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 1)) }, Period(), CleanupFilter.AllEvents, null));
        }

        [Fact]
        public void NullItemInSnapshot_Throws()
        {
            var items = new TargetItemInfo?[] { Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 1)), null };

            Assert.Throws<ArgumentException>(() =>
                CleanupPlanner.BuildPlan(items!, Period(), CleanupFilter.AllEvents, Array.Empty<TargetOccurrence>()));
        }
    }
}
