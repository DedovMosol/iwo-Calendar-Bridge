using System;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Клип серии к окну копирования: StartDate — вхождение по правилу (не понедельник окна), count пересчитывается,
    /// хвост не режется, отпечаток источника не меняется, исключения до нового старта отбрасываются.
    /// </summary>
    public sealed class RecurrenceClipTests
    {
        private static readonly TargetTimeZone Zone = TargetTimeZone.FromHours(3);

        // Среда. Exchange 2007 WeeklyPattern требует, чтобы StartDate попадал в DaysOfWeek.
        private static readonly DateTime Wednesday1949 = new DateTime(1949, 3, 16);

        private static RecurrenceRule WeeklyWed(DateTime start, int interval = 1, int? count = null, DateTime? end = null) =>
            new RecurrenceRule(RecurrencePattern.Weekly, interval, start, end, count, WeekDays.Wednesday);

        private static DateTime UtcDate(int year, int month, int day) =>
            new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void WeeklyWednesday_WindowMonday_StartsOnWednesdayNotMonday()
        {
            // 5 октября 2026 — понедельник; первое вхождение — среда 7-го.
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(WeeklyWed(Wednesday1949), UtcDate(2026, 10, 5), Zone);

            Assert.True(write.Clipped);
            Assert.Equal(new DateTime(2026, 10, 7), write.StartDate);
            Assert.True(write.Rule.NeverEnds);
            Assert.Equal(WeekDays.Wednesday, write.Rule.Days);
        }

        [Fact]
        public void AlreadyInWindow_Unchanged()
        {
            var rule = WeeklyWed(new DateTime(2026, 10, 7));
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(rule, UtcDate(2026, 10, 5), Zone);

            Assert.False(write.Clipped);
            Assert.Same(rule, write.Rule);
        }

        [Fact]
        public void DailyCount_RemainingAfterClip()
        {
            var rule = new RecurrenceRule(RecurrencePattern.Daily, 1, new DateTime(2026, 1, 1), occurrences: 100);
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(rule, UtcDate(2026, 2, 1), Zone);

            Assert.True(write.Clipped);
            Assert.Equal(new DateTime(2026, 2, 1), write.StartDate);
            Assert.Equal(69, write.Rule.Occurrences);
        }

        [Fact]
        public void Monthly31st_SkipsFebruary()
        {
            var rule = new RecurrenceRule(RecurrencePattern.MonthlyAbsolute, 1, new DateTime(2026, 1, 31), dayOfMonth: 31);
            DateTime? first = RecurrenceClip.FirstOnOrAfter(rule, new DateTime(2026, 2, 1));

            Assert.Equal(new DateTime(2026, 3, 31), first);
        }

        [Fact]
        public void RelativeMonthly_SecondThursday()
        {
            var rule = new RecurrenceRule(
                RecurrencePattern.MonthlyRelative, 1, new DateTime(2026, 1, 8), days: WeekDays.Thursday, week: WeekOfMonth.Second);
            DateTime? first = RecurrenceClip.FirstOnOrAfter(rule, new DateTime(2026, 2, 1));

            Assert.Equal(new DateTime(2026, 2, 12), first);
        }

        [Fact]
        public void MovedExceptionFromBeforeWindow_DoesNotUnclipSeries()
        {
            var rule = WeeklyWed(Wednesday1949);
            DateTime original = Zone.LocalToUtc(Wednesday1949.AddHours(8));
            DateTime moved = Zone.LocalToUtc(new DateTime(2026, 10, 10, 8, 0, 0));
            var exception = new SourceException(original, false, moved, moved.AddHours(1), "сдвиг");

            RecurrenceClip.Write write = RecurrenceClip.ForWrite(rule, UtcDate(2026, 10, 5), Zone);

            Assert.Equal(new DateTime(2026, 10, 7), write.StartDate);
            Assert.False(RecurrenceClip.IncludesException(write, exception, Zone));
        }

        [Fact]
        public void ExceptionBeforeClip_Excluded()
        {
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(WeeklyWed(Wednesday1949), UtcDate(2026, 10, 5), Zone);
            var old = new SourceException(Zone.LocalToUtc(new DateTime(2020, 1, 1, 8, 0, 0)), true);

            Assert.False(RecurrenceClip.IncludesException(write, old, Zone));
        }

        [Fact]
        public void SourceHash_UnchangedByClip()
        {
            var item = new SourceItem(
                "s",
                "Тема",
                "Зал",
                null,
                Zone.LocalToUtc(Wednesday1949.AddHours(8)),
                Zone.LocalToUtc(Wednesday1949.AddHours(9)),
                recurrence: WeeklyWed(Wednesday1949));
            string hash = item.ContentHash;

            RecurrenceClip.Write? write = RecurrenceClip.ForWrite(item, UtcDate(2026, 10, 5), Zone);

            Assert.True(write!.Clipped);
            Assert.Equal(hash, item.ContentHash);
            Assert.Equal(Wednesday1949, item.Recurrence!.StartDate);
        }

        [Fact]
        public void ShiftTimes_KeepsLocalTimeOfDay()
        {
            var item = new SourceItem(
                "s",
                "Тема",
                null,
                null,
                Zone.LocalToUtc(Wednesday1949.AddHours(8)),
                Zone.LocalToUtc(Wednesday1949.AddHours(9)),
                recurrence: WeeklyWed(Wednesday1949));

            (DateTime start, DateTime end) = RecurrenceClip.ShiftTimes(item, new DateTime(2026, 10, 7), Zone);

            Assert.Equal(new DateTime(2026, 10, 7, 5, 0, 0, DateTimeKind.Utc), start);
            Assert.Equal(TimeSpan.FromHours(1), end - start);
        }

        [Fact]
        public void WeeklyInterval2_KeepsPhase()
        {
            var original = WeeklyWed(Wednesday1949, interval: 2);
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(original, UtcDate(2026, 10, 5), Zone);
            DateTime start = write.StartDate;
            Assert.Equal(start, RecurrenceClip.FirstOnOrAfter(original, start));
            Assert.True(start >= new DateTime(2026, 10, 5));
            Assert.Equal(DayOfWeek.Wednesday, start.DayOfWeek);
            Assert.Equal(2, write.Rule.Interval);
        }

        [Fact]
        public void AllTimeWindow_DoesNotClip1949()
        {
            RecurrenceClip.Write write = RecurrenceClip.ForWrite(
                WeeklyWed(Wednesday1949),
                DateTime.SpecifyKind(TargetTimeZone.MinPeriodDate, DateTimeKind.Utc),
                Zone);

            Assert.False(write.Clipped);
            Assert.Equal(Wednesday1949, write.StartDate);
        }
    }
}
