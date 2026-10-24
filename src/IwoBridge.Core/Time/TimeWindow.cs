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
    }
}
