using System;
using System.Collections.Generic;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Чистое обогащение снимка: мастерам серий проставляются дочитанные границы повторений, UID и
    /// изменённые повторения. Одиночные события и мастера без дочитанных данных не меняются.
    /// Неизменяемая модель пересобирается (<see cref="TargetItemInfo.WithRecurrence"/>), исходный список
    /// не мутируется.
    /// </summary>
    public static class RecurrenceEnricher
    {
        public static IReadOnlyList<TargetItemInfo> Apply(
            IReadOnlyList<TargetItemInfo> items, IReadOnlyDictionary<string, RecurrenceInfo> recurrenceById)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (recurrenceById is null)
            {
                throw new ArgumentNullException(nameof(recurrenceById));
            }

            var result = new List<TargetItemInfo>(items.Count);
            foreach (TargetItemInfo item in items)
            {
                if (item.Kind == ItemKind.RecurringMaster
                    && recurrenceById.TryGetValue(item.ItemId, out RecurrenceInfo? info)
                    && info != null)
                {
                    result.Add(item.WithRecurrence(
                        info.FirstStartUtc ?? item.FirstOccurrenceUtc,
                        info.LastStartUtc,
                        info.Uid ?? item.Uid,
                        info.ModifiedOccurrences));
                }
                else
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }
}
