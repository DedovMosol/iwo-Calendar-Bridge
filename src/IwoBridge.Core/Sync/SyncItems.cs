using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Событие источника (основного календаря) для планировщика синхронизации: устойчивый идентификатор, вид,
    /// охват и отпечаток содержимого. Серия — одно событие (мастер): копируется и сравнивается целиком.
    /// </summary>
    public sealed class SourceEvent
    {
        /// <param name="sourceId">
        /// Устойчивый идентификатор в источнике (у Outlook — EntryID элемента; у серии — EntryID мастера). Тот же,
        /// что записывается в копию (<c>IwoBridgeSourceId</c>).
        /// </param>
        /// <param name="spanStartUtc">Начало охвата: события или первого повторения серии.</param>
        /// <param name="spanEndUtc">Конец охвата: события или последнего повторения; бессрочная серия — <see cref="DateTime.MaxValue"/>.</param>
        /// <param name="contentHash">Отпечаток <see cref="SyncContent.Hash"/>.</param>
        /// <param name="subject">Тема — только для показа человеку.</param>
        public SourceEvent(string sourceId, ItemKind kind, DateTime spanStartUtc, DateTime spanEndUtc, string contentHash, string? subject = null)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                throw new ArgumentException("Нужен идентификатор события источника.", nameof(sourceId));
            }

            if (string.IsNullOrEmpty(contentHash))
            {
                throw new ArgumentException("Нужен отпечаток содержимого.", nameof(contentHash));
            }

            SourceId = sourceId;
            Kind = kind;
            SpanStartUtc = TimeMapper.ToUtc(spanStartUtc);
            SpanEndUtc = spanEndUtc == DateTime.MaxValue ? DateTime.MaxValue : TimeMapper.ToUtc(spanEndUtc);
            ContentHash = contentHash;
            Subject = subject;
        }

        public string SourceId { get; }

        public ItemKind Kind { get; }

        public DateTime SpanStartUtc { get; }

        public DateTime SpanEndUtc { get; }

        public string ContentHash { get; }

        public string? Subject { get; }

        internal bool Overlaps(DateTime windowStartUtc, DateTime windowEndUtc) =>
            TimeWindow.Overlaps(SpanStartUtc, SpanEndUtc, windowStartUtc, windowEndUtc);
    }

    /// <summary>Копия, созданная мостом в календаре приёмника (несёт метку источника и отпечаток).</summary>
    public sealed class BridgeCopy
    {
        /// <param name="contentHash">Отпечаток при записи копии; <c>null</c> — неизвестен (копия старой версии).</param>
        public BridgeCopy(string itemId, string sourceId, ItemKind kind, DateTime spanStartUtc, DateTime spanEndUtc, string? contentHash, string? subject = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("Нужен ItemId копии.", nameof(itemId));
            }

            if (string.IsNullOrEmpty(sourceId))
            {
                throw new ArgumentException("У копии моста должна быть метка источника.", nameof(sourceId));
            }

            ItemId = itemId;
            SourceId = sourceId;
            Kind = kind;
            SpanStartUtc = TimeMapper.ToUtc(spanStartUtc);
            SpanEndUtc = spanEndUtc == DateTime.MaxValue ? DateTime.MaxValue : TimeMapper.ToUtc(spanEndUtc);
            ContentHash = string.IsNullOrEmpty(contentHash) ? null : contentHash;
            Subject = subject;
        }

        public string ItemId { get; }

        public string SourceId { get; }

        public ItemKind Kind { get; }

        public DateTime SpanStartUtc { get; }

        public DateTime SpanEndUtc { get; }

        public string? ContentHash { get; }

        public string? Subject { get; }

        internal bool Overlaps(DateTime windowStartUtc, DateTime windowEndUtc) =>
            TimeWindow.Overlaps(SpanStartUtc, SpanEndUtc, windowStartUtc, windowEndUtc);
    }

    /// <summary>
    /// Снимок источника за период. Контракт с адаптером источника:
    ///  - все события, пересекающие период;
    ///  - плюс все ещё существующие события, на которые ссылаются копии периода (событие могли перенести за
    ///    пределы периода — тогда копию нужно передвинуть, а не удалить). Отсутствие события в снимке означает
    ///    «удалено в источнике»;
    ///  - <see cref="IsComplete"/> = false, если чтение было неполным (обрыв, ошибка части элементов) — тогда
    ///    планировщик не удаляет ни одной копии.
    /// </summary>
    public sealed class SourceSnapshot
    {
        public SourceSnapshot(IEnumerable<SourceEvent> events, bool isComplete, string? incompleteReason = null)
        {
            Events = (events ?? throw new ArgumentNullException(nameof(events))).ToArray();
            IsComplete = isComplete;
            IncompleteReason = isComplete ? null : (incompleteReason ?? "Источник прочитан не полностью.");
        }

        public IReadOnlyList<SourceEvent> Events { get; }

        public bool IsComplete { get; }

        public string? IncompleteReason { get; }
    }
}
