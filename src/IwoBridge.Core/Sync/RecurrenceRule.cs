using System;
using System.Globalization;

namespace IwoBridge.Core.Sync
{
    /// <summary>Вид правила повторения — общий для Outlook и EWS.</summary>
    public enum RecurrencePattern
    {
        /// <summary>Каждые N дней.</summary>
        Daily,

        /// <summary>Каждые N недель по выбранным дням.</summary>
        Weekly,

        /// <summary>Каждые N месяцев в число месяца («15-го»).</summary>
        MonthlyAbsolute,

        /// <summary>Каждые N месяцев в день недели («во второй вторник»).</summary>
        MonthlyRelative,

        /// <summary>Каждый год в число месяца («15 мая»).</summary>
        YearlyAbsolute,

        /// <summary>Каждый год в день недели месяца («в последний понедельник мая»).</summary>
        YearlyRelative,
    }

    /// <summary>Дни недели. Значения — как у Outlook (olDaysOfWeek): адаптер Outlook передаёт маску без перевода.</summary>
    [Flags]
    public enum WeekDays
    {
        None = 0,
        Sunday = 1,
        Monday = 2,
        Tuesday = 4,
        Wednesday = 8,
        Thursday = 16,
        Friday = 32,
        Saturday = 64,
    }

    /// <summary>Неделя месяца для правил «во второй вторник»; значения — как у Outlook (Instance 1…5, 5 — последняя).</summary>
    public enum WeekOfMonth
    {
        None = 0,
        First = 1,
        Second = 2,
        Third = 3,
        Fourth = 4,
        Last = 5,
    }

    /// <summary>
    /// Правило повторения серии — то, что переносится в копию «настоящей серией». Даты правила — местные
    /// даты календаря (без времени): с какого дня серия начинается и до какого идёт. Время и длительность повторения
    /// — у самого события (начало и конец первого повторения).
    /// Окончание — одно из трёх: бессрочно, по дате (<see cref="EndDate"/>) или после N повторений
    /// (<see cref="Occurrences"/>).
    /// </summary>
    public sealed class RecurrenceRule
    {
        public RecurrenceRule(
            RecurrencePattern pattern,
            int interval,
            DateTime startDate,
            DateTime? endDate = null,
            int? occurrences = null,
            WeekDays days = WeekDays.None,
            int dayOfMonth = 0,
            int month = 0,
            WeekOfMonth week = WeekOfMonth.None)
        {
            if (interval < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "Интервал повторения — не меньше 1.");
            }

            if (endDate.HasValue && occurrences.HasValue)
            {
                throw new ArgumentException("Окончание серии — по дате или по числу повторений, не оба сразу.", nameof(occurrences));
            }

            if (occurrences.HasValue && occurrences.Value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrences));
            }

            if (endDate.HasValue && endDate.Value.Date < startDate.Date)
            {
                throw new ArgumentException("Дата окончания серии раньше её начала.", nameof(endDate));
            }

            bool needsDays = pattern == RecurrencePattern.Weekly || pattern == RecurrencePattern.MonthlyRelative || pattern == RecurrencePattern.YearlyRelative;
            if (needsDays && days == WeekDays.None)
            {
                throw new ArgumentException("Для этого правила нужны дни недели.", nameof(days));
            }

            bool needsDayOfMonth = pattern == RecurrencePattern.MonthlyAbsolute || pattern == RecurrencePattern.YearlyAbsolute;
            if (needsDayOfMonth && (dayOfMonth < 1 || dayOfMonth > 31))
            {
                throw new ArgumentOutOfRangeException(nameof(dayOfMonth));
            }

            bool needsMonth = pattern == RecurrencePattern.YearlyAbsolute || pattern == RecurrencePattern.YearlyRelative;
            if (needsMonth && (month < 1 || month > 12))
            {
                throw new ArgumentOutOfRangeException(nameof(month));
            }

            bool needsWeek = pattern == RecurrencePattern.MonthlyRelative || pattern == RecurrencePattern.YearlyRelative;
            if (needsWeek && week == WeekOfMonth.None)
            {
                throw new ArgumentException("Для этого правила нужна неделя месяца.", nameof(week));
            }

            Pattern = pattern;
            Interval = interval;
            StartDate = startDate.Date;
            EndDate = endDate?.Date;
            Occurrences = occurrences;
            Days = needsDays ? days : WeekDays.None;
            DayOfMonth = needsDayOfMonth ? dayOfMonth : 0;
            Month = needsMonth ? month : 0;
            Week = needsWeek ? week : WeekOfMonth.None;
        }

        public RecurrencePattern Pattern { get; }

        /// <summary>Каждые N дней, недель или месяцев (для годовых правил — 1).</summary>
        public int Interval { get; }

        /// <summary>Первый день серии (местная дата).</summary>
        public DateTime StartDate { get; }

        /// <summary>Последний день серии (местная дата); <c>null</c> — нет даты окончания.</summary>
        public DateTime? EndDate { get; }

        /// <summary>Число повторений; <c>null</c> — не по числу.</summary>
        public int? Occurrences { get; }

        public bool NeverEnds => EndDate is null && Occurrences is null;

        public WeekDays Days { get; }

        public int DayOfMonth { get; }

        public int Month { get; }

        public WeekOfMonth Week { get; }

        /// <summary>Каноническая запись — для отпечатка содержимого (одинаковые правила дают одинаковую запись).</summary>
        public string Canonical => string.Join(
            "|",
            Pattern.ToString(),
            Interval.ToString(CultureInfo.InvariantCulture),
            StartDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            EndDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "-",
            Occurrences?.ToString(CultureInfo.InvariantCulture) ?? "-",
            ((int)Days).ToString(CultureInfo.InvariantCulture),
            DayOfMonth.ToString(CultureInfo.InvariantCulture),
            Month.ToString(CultureInfo.InvariantCulture),
            ((int)Week).ToString(CultureInfo.InvariantCulture));
    }
}
