using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Клип правила серии к периоду копирования — только для записи в приёмник. Отпечаток считается по исходному
    /// правилу, иначе скользящее окно автокопирования каждый раз пересоздавало бы серию.
    ///
    /// Новая дата начала — первое вхождение по правилу не раньше окна. WeeklyPattern на Exchange 2007 получает
    /// StartDate в выбранный день недели, не понедельник окна. Исключения до этой даты не переносятся (иначе
    /// одно вхождение 1949-го снова раздуло бы серию). Хвост и «бессрочно» не режутся.
    /// </summary>
    public static class RecurrenceClip
    {
        private const int MaxSteps = 200000;

        /// <summary>Правило, которое можно отдать EWS, и нужно ли было сдвигать начало.</summary>
        public sealed class Write
        {
            internal Write(RecurrenceRule original, RecurrenceRule rule)
            {
                Original = original;
                Rule = rule;
            }

            public RecurrenceRule Original { get; }

            public RecurrenceRule Rule { get; }

            /// <summary>Дата начала, которую увидит приёмник (местная дата правила).</summary>
            public DateTime StartDate => Rule.StartDate;

            public bool Clipped => Original.StartDate != Rule.StartDate || Original.Occurrences != Rule.Occurrences;
        }

        /// <summary>
        /// Правило для записи; <c>null</c> — не серия. Если вхождений после окна нет (серия кончилась раньше),
        /// возвращается исходное правило: лучше полная серия, чем пустой Recurrence на Exchange 2007.
        /// </summary>
        public static Write? ForWrite(SourceItem item, DateTime windowStartUtc, TargetTimeZone zone)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            if (item.Recurrence is null)
            {
                return null;
            }

            return ForWrite(item.Recurrence, windowStartUtc, zone);
        }

        public static Write ForWrite(RecurrenceRule rule, DateTime windowStartUtc, TargetTimeZone zone)
        {
            if (rule is null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            DateTime notBefore = zone.UtcToLocal(windowStartUtc).Date;
            if (notBefore <= rule.StartDate)
            {
                return new Write(rule, rule);
            }

            // Не тянем старт к OriginalStart исключений до окна: одно перенесённое вхождение 1949-го
            // снова записало бы всю историю. Такое исключение просто не переносится (IncludesException).
            DateTime? first = FirstOnOrAfter(rule, notBefore);
            if (first is null || first.Value == rule.StartDate)
            {
                return new Write(rule, rule);
            }

            int skipped = CountBefore(rule, first.Value);
            int? occurrences = rule.Occurrences;
            if (occurrences.HasValue)
            {
                int remaining = occurrences.Value - skipped;
                if (remaining < 1)
                {
                    return new Write(rule, rule);
                }

                occurrences = remaining;
            }

            DateTime? end = rule.EndDate;
            if (end.HasValue && end.Value < first.Value)
            {
                return new Write(rule, rule);
            }

            var clipped = new RecurrenceRule(
                rule.Pattern,
                rule.Interval,
                first.Value,
                end,
                occurrences,
                rule.Days,
                rule.DayOfMonth,
                rule.Month,
                rule.Week);
            return new Write(rule, clipped);
        }

        /// <summary>Исключение переносится, только если его исходное начало не раньше клипнутого старта серии.</summary>
        public static bool IncludesException(Write write, SourceException exception, TargetTimeZone zone)
        {
            if (write is null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            return zone.UtcToLocal(exception.OriginalStartUtc).Date >= write.StartDate;
        }

        /// <summary>Начало/конец первого вхождения клипнутой серии: то же местное время суток и та же длительность.</summary>
        public static (DateTime StartUtc, DateTime EndUtc) ShiftTimes(SourceItem item, DateTime newLocalDate, TargetTimeZone zone)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            DateTime local = zone.UtcToLocal(item.StartUtc);
            DateTime shifted = item.IsAllDay
                ? newLocalDate.Date
                : newLocalDate.Date + local.TimeOfDay;
            DateTime startUtc = zone.LocalToUtc(shifted);
            TimeSpan duration = item.EndUtc - item.StartUtc;
            DateTime endUtc = startUtc.Ticks + duration.Ticks > DateTime.MaxValue.Ticks
                ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                : startUtc + duration;
            return (startUtc, endUtc);
        }

        public static DateTime? FirstOnOrAfter(RecurrenceRule rule, DateTime notBefore)
        {
            if (rule is null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            DateTime floor = notBefore.Date < rule.StartDate ? rule.StartDate : notBefore.Date;
            if (rule.EndDate.HasValue && floor > rule.EndDate.Value)
            {
                return null;
            }

            DateTime cursor = floor;
            for (int step = 0; step < MaxSteps; step++)
            {
                if (rule.EndDate.HasValue && cursor > rule.EndDate.Value)
                {
                    return null;
                }

                if (Matches(rule, cursor))
                {
                    return cursor;
                }

                DateTime next = Advance(cursor);
                if (next <= cursor)
                {
                    return null;
                }

                cursor = next;
            }

            return null;
        }

        internal static bool Matches(RecurrenceRule rule, DateTime date)
        {
            DateTime day = date.Date;
            if (day < rule.StartDate)
            {
                return false;
            }

            if (rule.EndDate.HasValue && day > rule.EndDate.Value)
            {
                return false;
            }

            switch (rule.Pattern)
            {
                case RecurrencePattern.Daily:
                    return (day - rule.StartDate).Days % rule.Interval == 0;
                case RecurrencePattern.Weekly:
                    return HasDay(rule.Days, day) && WeeksSince(rule.StartDate, day) % rule.Interval == 0;
                case RecurrencePattern.MonthlyAbsolute:
                    return MonthsSince(rule.StartDate, day) % rule.Interval == 0
                        && day.Day == rule.DayOfMonth
                        && DateTime.DaysInMonth(day.Year, day.Month) >= rule.DayOfMonth;
                case RecurrencePattern.MonthlyRelative:
                    return MonthsSince(rule.StartDate, day) % rule.Interval == 0 && IsNthWeekday(day, rule.Days, rule.Week);
                case RecurrencePattern.YearlyAbsolute:
                    return day.Month == rule.Month
                        && day.Day == rule.DayOfMonth
                        && DateTime.DaysInMonth(day.Year, day.Month) >= rule.DayOfMonth
                        && (day.Year - rule.StartDate.Year) % rule.Interval == 0;
                case RecurrencePattern.YearlyRelative:
                    return day.Month == rule.Month
                        && (day.Year - rule.StartDate.Year) % rule.Interval == 0
                        && IsNthWeekday(day, rule.Days, rule.Week);
                default:
                    return false;
            }
        }

        private static int CountBefore(RecurrenceRule rule, DateTime exclusive)
        {
            DateTime limit = exclusive.Date;
            if (limit <= rule.StartDate)
            {
                return 0;
            }

            if (rule.Pattern == RecurrencePattern.Daily)
            {
                int span = (limit - rule.StartDate).Days;
                if (span <= 0)
                {
                    return 0;
                }

                return ((span - 1) / rule.Interval) + 1;
            }

            int count = 0;
            DateTime cursor = rule.StartDate;
            for (int step = 0; step < MaxSteps && cursor < limit; step++)
            {
                if (Matches(rule, cursor))
                {
                    count++;
                }

                DateTime next = Advance(cursor);
                if (next <= cursor)
                {
                    break;
                }

                cursor = next;
            }

            return count;
        }

        private static DateTime Advance(DateTime day)
        {
            return day.Date < DateTime.MaxValue.Date ? day.Date.AddDays(1) : day;
        }

        /// <summary>
        /// Недели от якоря: календарная неделя с воскресенья — так Exchange 2007 хранит WeeklyPattern без FirstDayOfWeek.
        /// </summary>
        private static int WeeksSince(DateTime start, DateTime day)
        {
            DateTime Align(DateTime value) => value.Date.AddDays(-(int)value.DayOfWeek);
            return (Align(day) - Align(start)).Days / 7;
        }

        private static int MonthsSince(DateTime start, DateTime day) =>
            ((day.Year - start.Year) * 12) + day.Month - start.Month;

        private static bool HasDay(WeekDays days, DateTime date) =>
            (days & (WeekDays)(1 << (int)date.DayOfWeek)) != 0;

        private static bool IsNthWeekday(DateTime date, WeekDays days, WeekOfMonth week)
        {
            if (!HasDay(days, date))
            {
                return false;
            }

            if (week == WeekOfMonth.Last)
            {
                DateTime probe = date.AddDays(7);
                return probe.Month != date.Month;
            }

            int nth = 0;
            for (int d = 1; d <= date.Day; d++)
            {
                var cursor = new DateTime(date.Year, date.Month, d);
                if (HasDay(days, cursor))
                {
                    nth++;
                }
            }

            return nth == (int)week;
        }
    }
}
