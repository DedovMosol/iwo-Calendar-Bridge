using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.App.Cleanup
{
    /// <summary>Что именно удаляется строкой манифеста.</summary>
    public enum CleanupManifestEntryKind
    {
        /// <summary>Одиночное событие.</summary>
        Single,

        /// <summary>Серия целиком (удаляется мастер); время — первого повторения.</summary>
        WholeSeries,

        /// <summary>Отдельный экземпляр серии на границе периода (повторение или исключение).</summary>
        SeriesInstance,
    }

    /// <summary>
    /// Строка манифеста удаляемого: вид, идентификатор, тема и время (UTC). Манифест пишется до удаления и
    /// совпадает со списком удаления по построению (<see cref="CleanupRunner"/>).
    /// </summary>
    public sealed class CleanupManifestEntry
    {
        public CleanupManifestEntry(
            CleanupManifestEntryKind kind, string itemId, string? subject, DateTime startUtc, DateTime endUtc)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("ItemId обязателен.", nameof(itemId));
            }

            Kind = kind;
            ItemId = itemId;
            Subject = subject;
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
        }

        public CleanupManifestEntryKind Kind { get; }

        public string ItemId { get; }

        public string? Subject { get; }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        /// <summary>Элемент, удаляемый целиком: одиночное событие или мастер серии.</summary>
        public static CleanupManifestEntry ForItem(TargetItemInfo item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            CleanupManifestEntryKind kind = item.Kind == ItemKind.RecurringMaster
                ? CleanupManifestEntryKind.WholeSeries
                : CleanupManifestEntryKind.Single;
            return new CleanupManifestEntry(kind, item.ItemId, item.Subject, item.StartUtc, item.EndUtc);
        }

        /// <summary>
        /// Всё удаляемое по плану в порядке удаления: сначала целиком (одиночные и серии), затем экземпляры серий.
        /// Тот же список и те же слова, что окажутся в манифесте, — предпросмотр показывает его пользователю.
        /// </summary>
        public static IReadOnlyList<CleanupManifestEntry> ListOf(CleanupPlan plan)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var entries = new List<CleanupManifestEntry>(plan.DeleteCount + plan.OccurrenceDeleteCount);
            entries.AddRange(plan.ToDelete.Select(ForItem));
            entries.AddRange(plan.OccurrencesToDelete.Select(ForOccurrence));
            return entries;
        }

        /// <summary>Экземпляр граничной серии, удаляемый при обрезке.</summary>
        public static CleanupManifestEntry ForOccurrence(TargetOccurrence occurrence)
        {
            if (occurrence is null)
            {
                throw new ArgumentNullException(nameof(occurrence));
            }

            return new CleanupManifestEntry(
                CleanupManifestEntryKind.SeriesInstance,
                occurrence.OccurrenceItemId,
                occurrence.Subject,
                occurrence.StartUtc,
                occurrence.EndUtc);
        }
    }
}
