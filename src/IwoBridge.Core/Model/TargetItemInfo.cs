using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Model
{
    /// <summary>
    /// Неизменяемое описание элемента календаря приёмника в том виде, в каком его отдаёт неразвёрнутый
    /// <c>FindItem</c>: мастер серии или одиночное событие (без экземпляров). Границы серии, UID и
    /// изменённые повторения дочитываются отдельно и проставляются <see cref="Reading.RecurrenceEnricher"/>.
    /// Все моменты времени хранятся в UTC.
    /// </summary>
    public sealed class TargetItemInfo
    {
        public TargetItemInfo(
            string itemId,
            string? changeKey,
            string itemClass,
            ItemKind kind,
            DateTime startUtc,
            DateTime endUtc,
            DateTime? firstOccurrenceUtc,
            DateTime? lastOccurrenceUtc,
            string? bridgeSourceId,
            string? uid = null,
            IReadOnlyList<OccurrenceSpan>? modifiedOccurrences = null,
            string? subject = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("ItemId обязателен.", nameof(itemId));
            }

            ItemId = itemId;
            ChangeKey = changeKey;
            ItemClass = itemClass ?? string.Empty;
            Kind = kind;
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
            FirstOccurrenceUtc = firstOccurrenceUtc.HasValue ? TimeMapper.ToUtc(firstOccurrenceUtc.Value) : (DateTime?)null;
            LastOccurrenceUtc = lastOccurrenceUtc.HasValue ? TimeMapper.ToUtc(lastOccurrenceUtc.Value) : (DateTime?)null;
            BridgeSourceId = string.IsNullOrEmpty(bridgeSourceId) ? null : bridgeSourceId;
            Uid = string.IsNullOrEmpty(uid) ? null : uid;
            ModifiedOccurrences = modifiedOccurrences?.ToArray() ?? Array.Empty<OccurrenceSpan>();
            Subject = string.IsNullOrEmpty(subject) ? null : subject;
        }

        /// <summary>EWS ItemId.</summary>
        public string ItemId { get; }

        /// <summary>EWS ChangeKey (может отсутствовать).</summary>
        public string? ChangeKey { get; }

        /// <summary>Класс элемента, например <c>IPM.Appointment</c>.</summary>
        public string ItemClass { get; }

        public ItemKind Kind { get; }

        /// <summary>Начало элемента (для мастера — начало первого повторения по правилу).</summary>
        public DateTime StartUtc { get; }

        /// <summary>Конец элемента.</summary>
        public DateTime EndUtc { get; }

        /// <summary>Начало первого повторения серии (только у мастера).</summary>
        public DateTime? FirstOccurrenceUtc { get; }

        /// <summary>Начало последнего повторения серии; <c>null</c> — бессрочная серия (только у мастера).</summary>
        public DateTime? LastOccurrenceUtc { get; }

        /// <summary>Метка моста (<c>IwoBridgeSourceId</c>), если копия создана мостом; иначе <c>null</c>.</summary>
        public string? BridgeSourceId { get; }

        /// <summary>iCalendar UID (<c>calendar:UID</c>) — общий у мастера и его экземпляров; иначе <c>null</c>.</summary>
        public string? Uid { get; }

        /// <summary>Изменённые повторения серии (исключения) с их фактическими интервалами (только у мастера).</summary>
        public IReadOnlyList<OccurrenceSpan> ModifiedOccurrences { get; }

        /// <summary>
        /// Тема — только для показа человеку (предпросмотр, манифест удаляемого); в решениях не участвует.
        /// <c>null</c> — темы нет или она не прочитана.
        /// </summary>
        public string? Subject { get; }

        /// <summary>Относится ли элемент к событиям календаря.</summary>
        public bool IsAppointment =>
            ItemClass.StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase);

        /// <summary>Создан ли элемент мостом (несёт метку источника).</summary>
        public bool IsBridgeCopy => BridgeSourceId != null;

        /// <summary>
        /// Копия с дочитанными данными серии; остальные поля переносятся как есть. Копирование живёт рядом с
        /// полями, чтобы новое поле нельзя было потерять при обогащении снимка.
        /// </summary>
        public TargetItemInfo WithRecurrence(
            DateTime? firstOccurrenceUtc,
            DateTime? lastOccurrenceUtc,
            string? uid,
            IReadOnlyList<OccurrenceSpan>? modifiedOccurrences)
        {
            return new TargetItemInfo(
                ItemId,
                ChangeKey,
                ItemClass,
                Kind,
                StartUtc,
                EndUtc,
                firstOccurrenceUtc,
                lastOccurrenceUtc,
                BridgeSourceId,
                uid,
                modifiedOccurrences,
                Subject);
        }
    }
}
