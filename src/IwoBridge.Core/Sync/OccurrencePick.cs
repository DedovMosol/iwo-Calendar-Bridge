using System;
using System.Collections.Generic;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Выбор повторения своей серии из ответа CalendarView (Exchange 2007: раскрытое окно, без OccurrenceId по дате).
    /// Чужие события того же дня и одиночные с тем же временем не берутся: только экземпляр серии с тем же UID.
    /// </summary>
    public static class OccurrencePick
    {
        /// <summary>Окно FindAppointments вокруг ожидаемого начала — сутки в каждую сторону.</summary>
        public static readonly TimeSpan SearchWindow = TimeSpan.FromDays(1);

        /// <summary>
        /// Допуск для обычного события: соседние ежедневные вхождения в 24 часах, DST/смещение пояса — часы.
        /// События «на весь день» сравниваются по местной дате, не по этому допуску.
        /// </summary>
        public static readonly TimeSpan TimedTolerance = TimeSpan.FromHours(12);

        public readonly struct Candidate
        {
            public Candidate(string? uid, DateTime startUtc, bool isSeriesInstance)
            {
                Uid = uid;
                StartUtc = TimeMapper.ToUtc(startUtc);
                IsSeriesInstance = isSeriesInstance;
            }

            public string? Uid { get; }

            public DateTime StartUtc { get; }

            public bool IsSeriesInstance { get; }
        }

        /// <summary>Индекс подходящего вхождения; <c>null</c> — своего повторения в ответе нет.</summary>
        public static int? IndexOf(
            IReadOnlyList<Candidate> items,
            string seriesUid,
            DateTime expectedUtc,
            bool isAllDay,
            TargetTimeZone zone)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (string.IsNullOrEmpty(seriesUid))
            {
                return null;
            }

            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            DateTime expected = TimeMapper.ToUtc(expectedUtc);
            DateTime expectedLocalDate = zone.UtcToLocal(expected).Date;
            int? best = null;
            TimeSpan bestDistance = TimeSpan.MaxValue;

            for (int i = 0; i < items.Count; i++)
            {
                Candidate item = items[i];
                if (!item.IsSeriesInstance
                    || string.IsNullOrEmpty(item.Uid)
                    || !string.Equals(item.Uid, seriesUid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TimeSpan? distance;
                if (isAllDay)
                {
                    distance = zone.UtcToLocal(item.StartUtc).Date == expectedLocalDate
                        ? (item.StartUtc - expected).Duration()
                        : (TimeSpan?)null;
                }
                else
                {
                    TimeSpan timed = (item.StartUtc - expected).Duration();
                    distance = timed < TimedTolerance ? timed : (TimeSpan?)null;
                }

                if (distance is null || (best != null && distance.Value >= bestDistance))
                {
                    continue;
                }

                best = i;
                bestDistance = distance.Value;
            }

            return best;
        }
    }
}
