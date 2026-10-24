using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;

namespace IwoBridge.Core.Abstractions
{
    /// <summary>
    /// Удаление элементов приёмника по их идентификаторам. Реализуется адаптером EWS (DIP).
    /// Возвращает исход по каждому запрошенному идентификатору — строгую оценку делает
    /// <see cref="Paging.CompletenessGuard"/> поверх, в оркестраторе.
    /// </summary>
    public interface ITargetDeleter
    {
        Task<IReadOnlyList<DeleteResult>> DeleteAsync(
            IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken);
    }
}
