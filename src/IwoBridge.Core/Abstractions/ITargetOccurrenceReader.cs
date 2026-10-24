using System;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Reading;

namespace IwoBridge.Core.Abstractions
{
    /// <summary>
    /// Раскрытие повторений календаря в окне (CalendarView). Реализуется адаптером EWS; усечение
    /// обрабатывается дроблением окна, а неустранимое усечение помечает результат неполным.
    /// </summary>
    public interface ITargetOccurrenceReader
    {
        Task<OccurrenceReadResult> ReadOccurrencesAsync(
            DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken);
    }
}
