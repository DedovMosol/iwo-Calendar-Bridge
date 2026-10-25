using System;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Outlook.Tests
{
    /// <summary>
    /// Правила повторения Outlook → ядро: каждый вид, «каждый будний день» (ежедневное с маской), годовые с интервалом в
    /// месяцах, окончание по дате и бессрочное.
    /// </summary>
    public sealed class OutlookRecurrenceMapperTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 5); // понедельник

        [Fact]
        public void Daily_EveryNDays()
        {
            RecurrenceRule rule = OutlookRecurrenceMapper.ToRule(Pattern(0, interval: 3));

            Assert.Equal(RecurrencePattern.Daily, rule.Pattern);
            Assert.Equal(3, rule.Interval);
            Assert.True(rule.NeverEnds);
        }

        [Fact]
        public void Daily_WithMask_IsEveryWeekday()
        {
            RecurrenceRule rule = OutlookRecurrenceMapper.ToRule(Pattern(0, mask: 62));

            Assert.Equal(RecurrencePattern.Weekly, rule.Pattern);
            Assert.Equal(WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Friday, rule.Days);
        }

        [Fact]
        public void Weekly_WithEndDate()
        {
            RecurrenceRule rule = OutlookRecurrenceMapper.ToRule(Pattern(1, interval: 2, mask: 2 | 8, end: Start.AddMonths(3)));

            Assert.Equal(RecurrencePattern.Weekly, rule.Pattern);
            Assert.Equal(2, rule.Interval);
            Assert.Equal(WeekDays.Monday | WeekDays.Wednesday, rule.Days);
            Assert.Equal(Start.AddMonths(3), rule.EndDate);
        }

        [Fact]
        public void Weekly_WithoutMask_UsesStartDay()
        {
            Assert.Equal(WeekDays.Monday, OutlookRecurrenceMapper.ToRule(Pattern(1)).Days);
        }

        [Fact]
        public void Monthly_AbsoluteAndRelative()
        {
            RecurrenceRule absolute = OutlookRecurrenceMapper.ToRule(Pattern(2, dayOfMonth: 15));
            RecurrenceRule relative = OutlookRecurrenceMapper.ToRule(Pattern(3, mask: 16, instance: 2));

            Assert.Equal(RecurrencePattern.MonthlyAbsolute, absolute.Pattern);
            Assert.Equal(15, absolute.DayOfMonth);
            Assert.Equal(RecurrencePattern.MonthlyRelative, relative.Pattern);
            Assert.Equal(WeekDays.Thursday, relative.Days);
            Assert.Equal(WeekOfMonth.Second, relative.Week);
        }

        [Fact]
        public void Yearly_IntervalInMonthsIgnored()
        {
            RecurrenceRule absolute = OutlookRecurrenceMapper.ToRule(Pattern(5, interval: 12, dayOfMonth: 9, month: 5));
            RecurrenceRule relative = OutlookRecurrenceMapper.ToRule(Pattern(6, interval: 12, mask: 1, instance: 5, month: 3));

            Assert.Equal(1, absolute.Interval);
            Assert.Equal(5, absolute.Month);
            Assert.Equal(9, absolute.DayOfMonth);
            Assert.Equal(RecurrencePattern.YearlyRelative, relative.Pattern);
            Assert.Equal(WeekOfMonth.Last, relative.Week);
            Assert.Equal(WeekDays.Sunday, relative.Days);
        }

        [Fact]
        public void Yearly_EveryTwoYears_BecomesMonthlyEvery24()
        {
            // «Раз в два года, 9 мая» и «раз в три года, в последнее воскресенье марта»: интервал не теряется.
            RecurrenceRule absolute = OutlookRecurrenceMapper.ToRule(Pattern(5, interval: 24, dayOfMonth: 9, month: 5));
            RecurrenceRule relative = OutlookRecurrenceMapper.ToRule(Pattern(6, interval: 36, mask: 1, instance: 5, month: 3));
            Assert.Equal(RecurrencePattern.MonthlyAbsolute, absolute.Pattern);
            Assert.Equal(24, absolute.Interval);
            Assert.Equal(9, absolute.DayOfMonth);
            Assert.Equal(RecurrencePattern.MonthlyRelative, relative.Pattern);
            Assert.Equal(36, relative.Interval);
            Assert.Equal(WeekOfMonth.Last, relative.Week);
            Assert.NotEqual(OutlookRecurrenceMapper.ToRule(Pattern(5, interval: 12, dayOfMonth: 9, month: 5)).Canonical, absolute.Canonical);
        }

        [Fact]
        public void LocalToUtc_TimeInSpringForwardGap_ShiftedNotThrown()
        {
            // Пояс с переходом: 28.03.2010 02:00 → 03:00 (так жила Европа и Россия до 2011 года).
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), new DateTime(2010, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
            TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("test-dst", TimeSpan.FromHours(3), "test", "test", "test-dst", new[] { rule });

            DateTime utc = OutlookSource.LocalToUtc(new DateTime(2010, 3, 28, 2, 30, 0), zone);

            Assert.Equal(new DateTime(2010, 3, 27, 23, 30, 0, DateTimeKind.Utc), utc);
            Assert.Equal(new DateTime(2010, 6, 1, 6, 0, 0, DateTimeKind.Utc), OutlookSource.LocalToUtc(new DateTime(2010, 6, 1, 10, 0, 0), zone));
        }

        [Fact]
        public void UnknownType_Rejected()
        {
            Assert.Throws<NotSupportedException>(() => OutlookRecurrenceMapper.ToRule(Pattern(4)));
        }

        private static OutlookPattern Pattern(
            int type, int interval = 1, int mask = 0, int dayOfMonth = 0, int month = 0, int instance = 0, DateTime? end = null) => new OutlookPattern
        {
            RecurrenceType = type,
            Interval = interval,
            DayOfWeekMask = mask,
            DayOfMonth = dayOfMonth,
            MonthOfYear = month,
            Instance = instance,
            PatternStartDate = Start,
            PatternEndDate = end ?? new DateTime(4501, 1, 1),
            NoEndDate = end is null,
        };
    }
}
