using System;

namespace IwoBridge.Core.Time
{
    /// <summary>
    /// Единое правило попадания события в окно (DRY): им пользуются и планировщик очистки, и фильтр
    /// раскрытых экземпляров серий. Окно — полуинтервал [start, end).
    ///
    /// Событие с длительностью попадает в окно, если строго пересекает его
    /// (<c>start &lt; windowEnd</c> и <c>end &gt; windowStart</c>); событие нулевой длительности — по началу.
    /// EWS <c>CalendarView</c> возвращает надмножество: по документации не возвращаются только элементы,
    /// окончание которых раньше StartDate, то есть событие, заканчивающееся ровно в начале окна, в ответ
    /// попадает. Поэтому ответ сервера всегда дофильтровывается этим правилом.
    /// </summary>
    public static class TimeWindow
    {
        private static readonly TimeSpan HalfDay = TimeSpan.FromHours(12);

        public static bool Overlaps(DateTime startUtc, DateTime endUtc, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            DateTime start = TimeMapper.ToUtc(startUtc);
            DateTime end = TimeMapper.ToUtc(endUtc);
            DateTime windowStart = TimeMapper.ToUtc(windowStartUtc);
            DateTime windowEnd = TimeMapper.ToUtc(windowEndUtc);

            if (end <= start)
            {
                return start >= windowStart && start < windowEnd;
            }

            return start < windowEnd && end > windowStart;
        }

        /// <summary>
        /// То же правило с учётом событий «на весь день». Такое событие занимает местные сутки пояса, где его создали;
        /// пояс календаря (и границы периода) может отличаться на несколько часов — например, у организатора летнее
        /// время, а у календаря постоянное смещение, или смещение задано неточно. Тогда сутки события задевают соседний
        /// день периода на эти часы, и событие соседнего дня считалось бы «в периоде» — очистка удалила бы его. Поэтому
        /// событие «на весь день» сравнивается без половины суток с каждой стороны: оно в периоде, только если большая
        /// часть хотя бы одних его суток лежит в периоде. При расхождении поясов меньше 12 часов это ровно его дни.
        /// </summary>
        public static bool Overlaps(DateTime startUtc, DateTime endUtc, bool isAllDay, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            if (!isAllDay)
            {
                return Overlaps(startUtc, endUtc, windowStartUtc, windowEndUtc);
            }

            DateTime start = Shift(TimeMapper.ToUtc(startUtc), HalfDay);
            DateTime end = Shift(TimeMapper.ToUtc(endUtc), -HalfDay);
            return Overlaps(start, end < start ? start : end, windowStartUtc, windowEndUtc);
        }

        /// <summary>Сдвиг с насыщением на границах <see cref="DateTime"/> (без переполнения).</summary>
        private static DateTime Shift(DateTime value, TimeSpan delta)
        {
            long ticks = value.Ticks + delta.Ticks;
            return new DateTime(Math.Min(Math.Max(ticks, DateTime.MinValue.Ticks), DateTime.MaxValue.Ticks), value.Kind);
        }
    }
}
