using System;
using IwoBridge.Core.Sync;

namespace IwoBridge.Outlook
{
    /// <summary>Поля <c>RecurrencePattern</c> Outlook, нужные правилу (значения как в объектной модели).</summary>
    internal sealed class OutlookPattern
    {
        public int RecurrenceType { get; set; }

        public int Interval { get; set; }

        public int DayOfWeekMask { get; set; }

        public int DayOfMonth { get; set; }

        public int MonthOfYear { get; set; }

        public int Instance { get; set; }

        public DateTime PatternStartDate { get; set; }

        public DateTime PatternEndDate { get; set; }

        public bool NoEndDate { get; set; }
    }

    /// <summary>
    /// Правило повторения Outlook → правило ядра. Чистая функция — покрыта тестами без Outlook.
    ///  - olRecursDaily (0) с маской дней — это «каждый будний день»: Outlook хранит его как ежедневное с маской,
    ///    переносится еженедельным по этим дням;
    ///  - olRecursWeekly (1), olRecursMonthly (2), olRecursMonthNth (3), olRecursYearly (5), olRecursYearNth (6);
    ///  - у годовых Outlook отдаёт интервал в месяцах (12) — годовое правило всегда «каждый год»;
    ///  - окончание: NoEndDate — бессрочно, иначе по дате окончания (Outlook заполняет и дату, и число повторений;
    ///    дата однозначна).
    /// </summary>
    internal static class OutlookRecurrenceMapper
    {
        private const int Daily = 0;
        private const int Weekly = 1;
        private const int Monthly = 2;
        private const int MonthNth = 3;
        private const int Yearly = 5;
        private const int YearNth = 6;

        public static RecurrenceRule ToRule(OutlookPattern p)
        {
            if (p is null)
            {
                throw new ArgumentNullException(nameof(p));
            }

            int interval = Math.Max(1, p.Interval);
            DateTime start = p.PatternStartDate.Date;
            DateTime? end = p.NoEndDate ? (DateTime?)null : p.PatternEndDate.Date;
            var days = (WeekDays)p.DayOfWeekMask;
            switch (p.RecurrenceType)
            {
                case Daily when days != WeekDays.None:
                    return new RecurrenceRule(RecurrencePattern.Weekly, 1, start, end, days: days);
                case Daily:
                    return new RecurrenceRule(RecurrencePattern.Daily, interval, start, end);
                case Weekly:
                    return new RecurrenceRule(RecurrencePattern.Weekly, interval, start, end, days: days == WeekDays.None ? DayOf(start) : days);
                case Monthly:
                    return new RecurrenceRule(RecurrencePattern.MonthlyAbsolute, interval, start, end, dayOfMonth: p.DayOfMonth);
                case MonthNth:
                    return new RecurrenceRule(RecurrencePattern.MonthlyRelative, interval, start, end, days: days, week: (WeekOfMonth)p.Instance);
                case Yearly:
                    return new RecurrenceRule(RecurrencePattern.YearlyAbsolute, 1, start, end, dayOfMonth: p.DayOfMonth, month: p.MonthOfYear);
                case YearNth:
                    return new RecurrenceRule(RecurrencePattern.YearlyRelative, 1, start, end, days: days, month: p.MonthOfYear, week: (WeekOfMonth)p.Instance);
                default:
                    throw new NotSupportedException("Неизвестный вид повторения Outlook: " + p.RecurrenceType + ".");
            }
        }

        private static WeekDays DayOf(DateTime date) => (WeekDays)(1 << (int)date.DayOfWeek);
    }
}
