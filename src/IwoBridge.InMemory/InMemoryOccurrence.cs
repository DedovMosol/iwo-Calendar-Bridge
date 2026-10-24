using System;
using IwoBridge.Core.Time;

namespace IwoBridge.InMemory
{
    /// <summary>
    /// Экземпляр события в ин-мемори календаре: свой ItemId, начало и конец (UTC). <see cref="IsModified"/> —
    /// изменённое повторение серии (исключение), например перенесённое на другую дату. <see cref="Subject"/> —
    /// собственная тема экземпляра (у исключения может отличаться); <c>null</c> — тема события.
    /// </summary>
    public sealed class InMemoryOccurrence
    {
        public InMemoryOccurrence(
            string occurrenceItemId, DateTime startUtc, DateTime endUtc, bool isModified = false, string? subject = null)
        {
            if (string.IsNullOrEmpty(occurrenceItemId))
            {
                throw new ArgumentException("ItemId экземпляра обязателен.", nameof(occurrenceItemId));
            }

            DateTime start = TimeMapper.ToUtc(startUtc);
            DateTime end = TimeMapper.ToUtc(endUtc);
            if (end < start)
            {
                throw new ArgumentException("Конец экземпляра раньше начала.", nameof(endUtc));
            }

            OccurrenceItemId = occurrenceItemId;
            StartUtc = start;
            EndUtc = end;
            IsModified = isModified;
            Subject = subject;
        }

        public string OccurrenceItemId { get; }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        public bool IsModified { get; }

        public string? Subject { get; }
    }
}
