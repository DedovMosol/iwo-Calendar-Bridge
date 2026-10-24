using System;
using System.Collections.Generic;
using System.Linq;

namespace IwoBridge.InMemory
{
    /// <summary>
    /// Описание засеиваемого события для <see cref="InMemoryCalendar"/>: одиночное событие или мастер серии с
    /// явно заданными экземплярами (повторениями и исключениями).
    /// </summary>
    public sealed class InMemoryEvent
    {
        private InMemoryEvent(
            string itemId,
            string itemClass,
            bool isMaster,
            string? uid,
            string? bridgeSourceId,
            string? subject,
            IReadOnlyList<InMemoryOccurrence> occurrences)
        {
            ItemId = itemId;
            ItemClass = itemClass;
            IsMaster = isMaster;
            Uid = uid;
            BridgeSourceId = bridgeSourceId;
            Subject = subject;
            Occurrences = occurrences;
        }

        public string ItemId { get; }

        public string ItemClass { get; }

        public bool IsMaster { get; }

        public string? Uid { get; }

        public string? BridgeSourceId { get; }

        public string? Subject { get; }

        public IReadOnlyList<InMemoryOccurrence> Occurrences { get; }

        public static InMemoryEvent Single(
            string itemId,
            DateTime startUtc,
            DateTime endUtc,
            string itemClass = "IPM.Appointment",
            string? bridgeSourceId = null,
            string? uid = null,
            string? subject = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("ItemId обязателен.", nameof(itemId));
            }

            return new InMemoryEvent(
                itemId,
                itemClass,
                false,
                uid,
                bridgeSourceId,
                subject,
                new[] { new InMemoryOccurrence(itemId, startUtc, endUtc) });
        }

        public static InMemoryEvent Master(
            string itemId,
            string uid,
            IReadOnlyList<InMemoryOccurrence> occurrences,
            string itemClass = "IPM.Appointment",
            string? bridgeSourceId = null,
            string? subject = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("ItemId обязателен.", nameof(itemId));
            }

            if (string.IsNullOrEmpty(uid))
            {
                throw new ArgumentException("UID серии обязателен.", nameof(uid));
            }

            if (occurrences is null || occurrences.Count == 0)
            {
                throw new ArgumentException("У серии должен быть хотя бы один экземпляр.", nameof(occurrences));
            }

            if (occurrences.Select(o => o.OccurrenceItemId).Distinct(StringComparer.Ordinal).Count() != occurrences.Count)
            {
                throw new ArgumentException("ItemId экземпляров серии должны быть уникальны.", nameof(occurrences));
            }

            return new InMemoryEvent(itemId, itemClass, true, uid, bridgeSourceId, subject, occurrences.ToArray());
        }
    }
}
