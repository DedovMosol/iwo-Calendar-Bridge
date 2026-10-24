using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Объём очистки: весь календарь либо период [StartUtc, EndUtcExclusive).
    /// Период задаётся в UTC; построение из местных дат — через <see cref="TargetTimeZone.PeriodFromLocalDates"/>.
    /// </summary>
    public sealed class CleanupScope
    {
        private CleanupScope(bool isWholeCalendar, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            IsWholeCalendar = isWholeCalendar;
            WindowStartUtc = windowStartUtc;
            WindowEndUtc = windowEndUtc;
        }

        public bool IsWholeCalendar { get; }

        /// <summary>Начало окна (включительно). Значимо только при <see cref="IsWholeCalendar"/> == false.</summary>
        public DateTime WindowStartUtc { get; }

        /// <summary>Конец окна (исключительно).</summary>
        public DateTime WindowEndUtc { get; }

        public static CleanupScope WholeCalendar()
        {
            return new CleanupScope(true, default, default);
        }

        public static CleanupScope Period(DateTime startUtc, DateTime endUtcExclusive)
        {
            DateTime start = TimeMapper.ToUtc(startUtc);
            DateTime end = TimeMapper.ToUtc(endUtcExclusive);
            if (end <= start)
            {
                throw new ArgumentException("Конец окна должен быть позже начала.", nameof(endUtcExclusive));
            }

            return new CleanupScope(false, start, end);
        }
    }
}
