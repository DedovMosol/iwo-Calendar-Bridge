using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Sync;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>
    /// Правило повторения ядра ↔ объекты повторения EWS Managed API. Чистые функции без сети — покрыты
    /// тестами в обе стороны. Окончание: «бессрочно» — <c>NeverEnds</c>, по дате — <c>EndDate</c>, по числу —
    /// <c>NumberOfOccurrences</c>.
    /// Правила «во второй вторник» в EWS задаются одним днём (<see cref="DayOfTheWeek"/>), включая особые значения
    /// «день», «будний день», «выходной»; иной набор дней для них не выражается — такое правило отклоняется с понятным
    /// текстом, а не переносится искажённым.
    /// </summary>
    public static class EwsRecurrenceMapper
    {
        private static readonly (WeekDays Flag, DayOfTheWeek Day)[] Days =
        {
            (WeekDays.Sunday, DayOfTheWeek.Sunday),
            (WeekDays.Monday, DayOfTheWeek.Monday),
            (WeekDays.Tuesday, DayOfTheWeek.Tuesday),
            (WeekDays.Wednesday, DayOfTheWeek.Wednesday),
            (WeekDays.Thursday, DayOfTheWeek.Thursday),
            (WeekDays.Friday, DayOfTheWeek.Friday),
            (WeekDays.Saturday, DayOfTheWeek.Saturday),
        };

        private const WeekDays Weekdays = WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Friday;
        private const WeekDays Weekend = WeekDays.Saturday | WeekDays.Sunday;
        private const WeekDays EveryDay = Weekdays | Weekend;

        public static Recurrence ToEws(RecurrenceRule rule)
        {
            if (rule is null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            Recurrence recurrence;
            switch (rule.Pattern)
            {
                case RecurrencePattern.Daily:
                    recurrence = new Recurrence.DailyPattern(rule.StartDate, rule.Interval);
                    break;
                case RecurrencePattern.Weekly:
                    recurrence = new Recurrence.WeeklyPattern(rule.StartDate, rule.Interval, DaysOf(rule.Days));
                    break;
                case RecurrencePattern.MonthlyAbsolute:
                    recurrence = new Recurrence.MonthlyPattern(rule.StartDate, rule.Interval, rule.DayOfMonth);
                    break;
                case RecurrencePattern.MonthlyRelative:
                    recurrence = new Recurrence.RelativeMonthlyPattern(rule.StartDate, rule.Interval, SingleDay(rule.Days), IndexOf(rule.Week));
                    break;
                case RecurrencePattern.YearlyAbsolute:
                    recurrence = new Recurrence.YearlyPattern(rule.StartDate, (Month)rule.Month, rule.DayOfMonth);
                    break;
                case RecurrencePattern.YearlyRelative:
                    recurrence = new Recurrence.RelativeYearlyPattern(rule.StartDate, (Month)rule.Month, SingleDay(rule.Days), IndexOf(rule.Week));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rule), rule.Pattern, "Неизвестный вид повторения.");
            }

            if (rule.EndDate.HasValue)
            {
                recurrence.EndDate = rule.EndDate.Value;
            }
            else if (rule.Occurrences.HasValue)
            {
                recurrence.NumberOfOccurrences = rule.Occurrences.Value;
            }
            else
            {
                recurrence.NeverEnds();
            }

            return recurrence;
        }

        public static RecurrenceRule FromEws(Recurrence recurrence)
        {
            if (recurrence is null)
            {
                throw new ArgumentNullException(nameof(recurrence));
            }

            DateTime start = recurrence.StartDate;
            DateTime? end = recurrence.HasEnd && recurrence.EndDate.HasValue ? recurrence.EndDate : null;
            int? count = recurrence.HasEnd && recurrence.NumberOfOccurrences.HasValue ? recurrence.NumberOfOccurrences : null;
            switch (recurrence)
            {
                case Recurrence.DailyPattern daily:
                    return new RecurrenceRule(RecurrencePattern.Daily, daily.Interval, start, end, count);
                case Recurrence.WeeklyPattern weekly:
                    return new RecurrenceRule(RecurrencePattern.Weekly, weekly.Interval, start, end, count, FlagsOf(weekly.DaysOfTheWeek));
                case Recurrence.MonthlyPattern monthly:
                    return new RecurrenceRule(RecurrencePattern.MonthlyAbsolute, monthly.Interval, start, end, count, dayOfMonth: monthly.DayOfMonth);
                case Recurrence.RelativeMonthlyPattern relativeMonthly:
                    return new RecurrenceRule(
                        RecurrencePattern.MonthlyRelative, relativeMonthly.Interval, start, end, count,
                        FlagOf(relativeMonthly.DayOfTheWeek), week: WeekOf(relativeMonthly.DayOfTheWeekIndex));
                case Recurrence.YearlyPattern yearly:
                    return new RecurrenceRule(RecurrencePattern.YearlyAbsolute, 1, start, end, count, dayOfMonth: yearly.DayOfMonth, month: (int)yearly.Month);
                case Recurrence.RelativeYearlyPattern relativeYearly:
                    return new RecurrenceRule(
                        RecurrencePattern.YearlyRelative, 1, start, end, count,
                        FlagOf(relativeYearly.DayOfTheWeek), month: (int)relativeYearly.Month, week: WeekOf(relativeYearly.DayOfTheWeekIndex));
                default:
                    // Регенерирующие правила («через N дней после завершения») — для задач, не для событий.
                    throw new NotSupportedException("Этот вид повторения не переносится: " + recurrence.GetType().Name + ".");
            }
        }

        private static DayOfTheWeek[] DaysOf(WeekDays days) => Days.Where(d => (days & d.Flag) != 0).Select(d => d.Day).ToArray();

        private static WeekDays FlagsOf(IEnumerable<DayOfTheWeek> days)
        {
            WeekDays result = WeekDays.None;
            foreach (DayOfTheWeek day in days)
            {
                result |= FlagOf(day);
            }

            return result;
        }

        private static WeekDays FlagOf(DayOfTheWeek day)
        {
            switch (day)
            {
                case DayOfTheWeek.Day:
                    return EveryDay;
                case DayOfTheWeek.Weekday:
                    return Weekdays;
                case DayOfTheWeek.WeekendDay:
                    return Weekend;
                default:
                    return Days.Single(d => d.Day == day).Flag;
            }
        }

        private static DayOfTheWeek SingleDay(WeekDays days)
        {
            switch (days)
            {
                case EveryDay:
                    return DayOfTheWeek.Day;
                case Weekdays:
                    return DayOfTheWeek.Weekday;
                case Weekend:
                    return DayOfTheWeek.WeekendDay;
            }

            (WeekDays Flag, DayOfTheWeek Day)[] single = Days.Where(d => d.Flag == days).ToArray();
            if (single.Length != 1)
            {
                throw new NotSupportedException("Правило «в N-й день недели месяца» с таким набором дней в Exchange не выражается.");
            }

            return single[0].Day;
        }

        private static DayOfTheWeekIndex IndexOf(WeekOfMonth week)
        {
            switch (week)
            {
                case WeekOfMonth.First:
                    return DayOfTheWeekIndex.First;
                case WeekOfMonth.Second:
                    return DayOfTheWeekIndex.Second;
                case WeekOfMonth.Third:
                    return DayOfTheWeekIndex.Third;
                case WeekOfMonth.Fourth:
                    return DayOfTheWeekIndex.Fourth;
                case WeekOfMonth.Last:
                    return DayOfTheWeekIndex.Last;
                default:
                    throw new ArgumentOutOfRangeException(nameof(week));
            }
        }

        private static WeekOfMonth WeekOf(DayOfTheWeekIndex index)
        {
            switch (index)
            {
                case DayOfTheWeekIndex.First:
                    return WeekOfMonth.First;
                case DayOfTheWeekIndex.Second:
                    return WeekOfMonth.Second;
                case DayOfTheWeekIndex.Third:
                    return WeekOfMonth.Third;
                case DayOfTheWeekIndex.Fourth:
                    return WeekOfMonth.Fourth;
                default:
                    return WeekOfMonth.Last;
            }
        }
    }
}
