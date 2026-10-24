using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Collections;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;

namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Оркестратор удаления: режет список на пакеты и применяет строгую оценку каждого пакета
    /// (<see cref="CompletenessGuard.EvaluateBatchDelete"/>). При первом же неполном пакете
    /// останавливается и не маскирует частичный результат (PLAN.md §8, T1/T30). Сетевые вызовы — за
    /// портом <see cref="ITargetDeleter"/>, поэтому класс тестируется без сервера.
    ///
    /// Разрушительная операция допустима только по авторитетному снимку: вызывающий код передаёт вердикт снимка
    /// (T2); при неполном снимке — отказ без удаления. Это второй рубеж: первый — сценарий, который по неполным
    /// данным до удаления не доходит.
    ///
    /// Повторы временных ошибок сервера (таймаут, «занят») выполняет адаптер (<see cref="Errors.RetryPolicy"/>):
    /// удаление идемпотентно, уже удалённое при повторе вернётся как «уже отсутствует».
    /// </summary>
    public sealed class CleanupExecutor
    {
        private const int MinBatchSize = 1;
        private const int MaxBatchSize = 1000;

        private readonly int _batchSize;

        public CleanupExecutor(int batchSize = 50)
        {
            if (batchSize < MinBatchSize || batchSize > MaxBatchSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(batchSize), batchSize, $"Размер пакета должен быть в пределах {MinBatchSize}..{MaxBatchSize}.");
            }

            _batchSize = batchSize;
        }

        public Task<BatchDeleteOutcome> DeleteAllAsync(
            IReadOnlyList<string> itemIds,
            ITargetDeleter deleter,
            TargetDeleteMode mode,
            bool snapshotIsAuthoritative,
            CancellationToken cancellationToken)
        {
            return DeleteAllAsync(itemIds, deleter, mode, snapshotIsAuthoritative, null, cancellationToken);
        }

        /// <param name="progress">
        /// Сколько учтено (удалено или уже отсутствовало) после каждого успешного пакета, нарастающим итогом;
        /// <c>null</c> — не сообщать.
        /// </param>
        /// <param name="cancellationToken">
        /// Остановка по просьбе пользователя. Проверяется перед каждым пакетом; начатый пакет всегда доводится до
        /// конца (в порт удаления токен не передаётся — запрос EWS всё равно не прерывается, а так «пакет начат» =
        /// «пакет выполнен»). Результат — <see cref="BatchDeleteOutcome.Cancelled"/> с точным числом удалённого,
        /// а не исключение: вызывающему нужен этот счёт.
        /// </param>
        public async Task<BatchDeleteOutcome> DeleteAllAsync(
            IReadOnlyList<string> itemIds,
            ITargetDeleter deleter,
            TargetDeleteMode mode,
            bool snapshotIsAuthoritative,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            if (itemIds is null)
            {
                throw new ArgumentNullException(nameof(itemIds));
            }

            if (deleter is null)
            {
                throw new ArgumentNullException(nameof(deleter));
            }

            if (!snapshotIsAuthoritative)
            {
                return BatchDeleteOutcome.Failure(
                    "Удаление отменено: снимок приёмника неполный, список мог быть недочитан (T2).");
            }

            if (itemIds.Count == 0)
            {
                return BatchDeleteOutcome.Success(0);
            }

            int removed = 0;
            foreach (IReadOnlyList<string> batch in Batching.Chunk(itemIds, _batchSize))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return BatchDeleteOutcome.Cancelled(removed);
                }

                IReadOnlyList<DeleteResult> results;
                try
                {
                    results = await deleter.DeleteAsync(batch, mode, CancellationToken.None).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Порт удаления вернул пустой ответ.");
                }
#pragma warning disable CA1031 // Любой сбой порта — исход с точным счётом; исключение сохраняется для показа причины.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Сервер мог успеть удалить часть оборванного пакета; повторный запуск доведёт дело, а уже
                    // удалённое засчитает как «нет такого элемента».
                    return BatchDeleteOutcome.Failure(
                        $"Учтено до остановки: {removed}. Пакет оборвался, часть его могла быть удалена — "
                        + $"запустите очистку снова. Причина: {ex.Message}",
                        removed,
                        ex);
                }

                BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, batch.Count);
                if (!outcome.IsSuccess)
                {
                    int accounted = removed + outcome.RemovedCount;
                    return BatchDeleteOutcome.Failure(
                        $"Учтено до остановки: {accounted}. Пакет не выполнен: {outcome.Error}", accounted);
                }

                removed += outcome.RemovedCount;
                ReportQuietly(progress, removed);
            }

            return BatchDeleteOutcome.Success(removed);
        }

        /// <summary>
        /// Сообщение о ходе — справочное: сбой его получателя не должен обрывать удаление посреди списка и терять счёт
        /// удалённого (манифест уже записан, а итог с числами вызывающему нужен).
        /// </summary>
        private static void ReportQuietly(IProgress<int>? progress, int removed)
        {
            if (progress is null)
            {
                return;
            }

            try
            {
                progress.Report(removed);
            }
#pragma warning disable CA1031 // Ошибка в индикаторе хода не важнее точного итога удаления.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
