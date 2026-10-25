using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Model
{
    /// <summary>
    /// Элемент раскрытого окна календаря (результат CalendarView): одиночное событие либо экземпляр серии
    /// (повторение или исключение). Несёт собственный ItemId (для удаления), UID серии (для сопоставления
    /// с мастером), начало и конец в UTC. В обрезку серий попадают только экземпляры серий
    /// (<see cref="IsSeriesInstance"/>), одиночные события — никогда.
    /// </summary>
    public sealed class TargetOccurrence
    {
        public TargetOccurrence(
            string occurrenceItemId,
            string? seriesUid,
            DateTime startUtc,
            DateTime endUtc,
            bool isSeriesInstance,
            string? subject = null,
            bool isAllDay = false)
        {
            IsAllDay = isAllDay;
            if (string.IsNullOrEmpty(occurrenceItemId))
            {
                throw new ArgumentException("ItemId экземпляра обязателен.", nameof(occurrenceItemId));
            }

            OccurrenceItemId = occurrenceItemId;
            SeriesUid = string.IsNullOrEmpty(seriesUid) ? null : seriesUid;
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
            IsSeriesInstance = isSeriesInstance;
            Subject = string.IsNullOrEmpty(subject) ? null : subject;
        }

        public string OccurrenceItemId { get; }

        public string? SeriesUid { get; }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        /// <summary>Экземпляр серии (повторение или исключение), а не одиночное событие.</summary>
        public bool IsSeriesInstance { get; }

        /// <summary>
        /// Тема экземпляра (у исключения может отличаться от темы серии) — только для показа человеку.
        /// </summary>
        public string? Subject { get; }

        /// <summary>Повторение «на весь день»: в период попадает по своим суткам (<see cref="Time.TimeWindow"/>).</summary>
        public bool IsAllDay { get; }
    }
}
