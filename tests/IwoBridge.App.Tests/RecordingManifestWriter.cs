using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Тестовый двойник порта манифеста: запоминает каждую запись, может выполнить проверку в момент записи
    /// (например, «ещё ничего не удалено») и может имитировать отказ — диск переполнен, нет прав.
    /// Файлов не создаёт: формат файла проверяют тесты <see cref="CsvCleanupManifestWriter"/>.
    /// </summary>
    internal sealed class RecordingManifestWriter : ICleanupManifestWriter
    {
        private readonly Exception? _failure;
        private readonly Action? _onWrite;

        /// <param name="failure">Исключение, которым завершится запись; <c>null</c> — запись успешна.</param>
        /// <param name="onWrite">Вызывается в момент записи, до возврата управления сценарию.</param>
        public RecordingManifestWriter(Exception? failure = null, Action? onWrite = null)
        {
            _failure = failure;
            _onWrite = onWrite;
        }

        /// <summary>Строки каждой выполненной записи в порядке вызовов.</summary>
        public List<IReadOnlyList<CleanupManifestEntry>> Writes { get; } = new List<IReadOnlyList<CleanupManifestEntry>>();

        public Task<string> WriteAsync(IReadOnlyList<CleanupManifestEntry> entries, CancellationToken cancellationToken)
        {
            _onWrite?.Invoke();
            if (_failure != null)
            {
                return Task.FromException<string>(_failure);
            }

            Writes.Add(entries.ToArray());
            return Task.FromResult("memory://manifest-" + Writes.Count);
        }
    }
}
