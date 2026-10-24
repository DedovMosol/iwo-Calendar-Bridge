using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Результат планирования очистки (чистый, без побочных эффектов): что удалить целиком, какие серии на
    /// границе периода обрезаются и какие именно их экземпляры удаляются, что пропущено и почему.
    /// </summary>
    public sealed class CleanupPlan
    {
        public CleanupPlan(
            IReadOnlyList<TargetItemInfo> toDelete,
            IReadOnlyList<TargetItemInfo> boundarySeriesToTrim,
            IReadOnlyList<TargetOccurrence> occurrencesToDelete,
            IReadOnlyList<SkippedItem> skipped)
        {
            ToDelete = (toDelete ?? throw new ArgumentNullException(nameof(toDelete))).ToArray();
            BoundarySeriesToTrim =
                (boundarySeriesToTrim ?? throw new ArgumentNullException(nameof(boundarySeriesToTrim))).ToArray();
            OccurrencesToDelete =
                (occurrencesToDelete ?? throw new ArgumentNullException(nameof(occurrencesToDelete))).ToArray();
            OccurrenceIdsToDelete = OccurrencesToDelete.Select(o => o.OccurrenceItemId).ToArray();
            Skipped = (skipped ?? throw new ArgumentNullException(nameof(skipped))).ToArray();
        }

        /// <summary>Одиночные события и мастера серий, удаляемые целиком.</summary>
        public IReadOnlyList<TargetItemInfo> ToDelete { get; }

        /// <summary>Серии на границе периода: у них удаляются только экземпляры внутри периода.</summary>
        public IReadOnlyList<TargetItemInfo> BoundarySeriesToTrim { get; }

        /// <summary>Экземпляры серий (повторения и исключения), удаляемые при обрезке, — как их отдало окно.</summary>
        public IReadOnlyList<TargetOccurrence> OccurrencesToDelete { get; }

        /// <summary>Идентификаторы <see cref="OccurrencesToDelete"/> в том же порядке.</summary>
        public IReadOnlyList<string> OccurrenceIdsToDelete { get; }

        /// <summary>Пропущенные элементы с причинами.</summary>
        public IReadOnlyList<SkippedItem> Skipped { get; }

        public int DeleteCount => ToDelete.Count;

        public int TrimCount => BoundarySeriesToTrim.Count;

        public int OccurrenceDeleteCount => OccurrencesToDelete.Count;

        public int SkipCount => Skipped.Count;

        public int TotalConsidered => DeleteCount + TrimCount + SkipCount;

        public bool IsEmpty => DeleteCount == 0 && OccurrenceDeleteCount == 0;
    }
}
