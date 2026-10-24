using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Результат раскрытия повторений в окне: сами повторения и признак полноты. Неполный результат
    /// (усечение CalendarView, которое не удалось раздробить) запрещает обрезку (T15).
    /// </summary>
    public sealed class OccurrenceReadResult
    {
        public OccurrenceReadResult(IReadOnlyList<TargetOccurrence> occurrences, bool isComplete)
        {
            Occurrences = (occurrences ?? throw new ArgumentNullException(nameof(occurrences))).ToArray();
            IsComplete = isComplete;
        }

        public IReadOnlyList<TargetOccurrence> Occurrences { get; }

        public bool IsComplete { get; }
    }
}
