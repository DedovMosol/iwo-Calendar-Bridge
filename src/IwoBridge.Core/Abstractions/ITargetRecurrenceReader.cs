using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Reading;

namespace IwoBridge.Core.Abstractions
{
    /// <summary>
    /// Дочитывание данных повторяющихся мастеров (границы повторений и UID) по их идентификаторам.
    /// Реализуется адаптером EWS через <c>GetItem</c> (эти свойства не возвращаются неразвёрнутым
    /// перечислением). Нужно только в режиме «период».
    /// </summary>
    public interface ITargetRecurrenceReader
    {
        Task<IReadOnlyDictionary<string, RecurrenceInfo>> LoadAsync(
            IReadOnlyList<string> masterItemIds, CancellationToken cancellationToken);
    }
}
