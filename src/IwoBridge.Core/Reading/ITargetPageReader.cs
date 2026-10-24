using System.Threading;
using System.Threading.Tasks;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Единственная граница с сетью при перечислении: отдаёт одну страницу календаря приёмника.
    /// Реализуется адаптером EWS (DIP). Оркестрацию (цикл, дедупликация, проверка полноты) делает
    /// <see cref="CalendarSnapshotReader"/> и тестируется с подменным читателем, без сервера.
    /// </summary>
    public interface ITargetPageReader
    {
        Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken);
    }
}
