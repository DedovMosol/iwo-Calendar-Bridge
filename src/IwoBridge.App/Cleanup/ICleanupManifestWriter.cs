using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Порт записи манифеста удаляемого (PLAN.md §8, T30). Вызывается до первого удаления; реализация
    /// обязана вернуть управление только после того, как манифест сохранён надёжно и целиком.
    /// Отказ среды (нет места, нет прав, файл занят) — <see cref="System.IO.IOException"/> или
    /// <see cref="System.UnauthorizedAccessException"/>: сценарий тогда отказывается удалять.
    /// </summary>
    public interface ICleanupManifestWriter
    {
        /// <summary>Записывает манифест и возвращает его расположение (для файла — полный путь).</summary>
        Task<string> WriteAsync(IReadOnlyList<CleanupManifestEntry> entries, CancellationToken cancellationToken);
    }
}
