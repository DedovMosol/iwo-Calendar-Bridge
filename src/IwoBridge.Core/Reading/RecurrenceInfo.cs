using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Данные мастера серии, дочитываемые отдельно (через <c>GetItem</c>): начало первого и последнего
    /// повторения, iCalendar UID и изменённые повторения. Нужны для классификации серий в режиме «период»
    /// и для сопоставления экземпляров при обрезке. <see cref="LastStartUtc"/> == <c>null</c> — бессрочная серия.
    /// </summary>
    public sealed class RecurrenceInfo
    {
        public RecurrenceInfo(
            DateTime? firstStartUtc,
            DateTime? lastStartUtc,
            string? uid,
            IReadOnlyList<OccurrenceSpan>? modifiedOccurrences = null)
        {
            FirstStartUtc = firstStartUtc.HasValue ? TimeMapper.ToUtc(firstStartUtc.Value) : (DateTime?)null;
            LastStartUtc = lastStartUtc.HasValue ? TimeMapper.ToUtc(lastStartUtc.Value) : (DateTime?)null;
            Uid = string.IsNullOrEmpty(uid) ? null : uid;
            ModifiedOccurrences = modifiedOccurrences?.ToArray() ?? Array.Empty<OccurrenceSpan>();
        }

        public DateTime? FirstStartUtc { get; }

        public DateTime? LastStartUtc { get; }

        public string? Uid { get; }

        public IReadOnlyList<OccurrenceSpan> ModifiedOccurrences { get; }
    }
}
