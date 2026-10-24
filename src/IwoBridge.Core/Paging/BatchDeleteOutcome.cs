using System;

namespace IwoBridge.Core.Paging
{
    /// <summary>
    /// Итог пакетного удаления. Успех — только если учтены все запрошенные id (удалено либо уже
    /// отсутствовало). Частичный результат — ошибка, не маскируется (T1, PLAN.md §3); при этом
    /// <see cref="RemovedCount"/> сообщает, сколько элементов было учтено до остановки.
    /// Остановка по просьбе пользователя — отдельный исход <see cref="IsCancelled"/>: не ошибка, но и не успех;
    /// <see cref="RemovedCount"/> — точное число удалённого до остановки.
    /// </summary>
    public readonly struct BatchDeleteOutcome
    {
        private BatchDeleteOutcome(bool isSuccess, bool isCancelled, int removedCount, string? error, Exception? exception)
        {
            IsSuccess = isSuccess;
            IsCancelled = isCancelled;
            RemovedCount = removedCount;
            Error = error;
            Exception = exception;
        }

        public bool IsSuccess { get; }

        /// <summary>Остановлено по отмене между пакетами; оставшиеся пакеты не отправлялись.</summary>
        public bool IsCancelled { get; }

        /// <summary>Сколько элементов удалено или уже отсутствовало (для ошибки и отмены — учтено до остановки).</summary>
        public int RemovedCount { get; }

        public string? Error { get; }

        /// <summary>
        /// Исключение порта удаления (обрыв сети, отказ в доступе), если пакет оборвался им. Сохраняется, чтобы
        /// приложение показало понятную причину (по виду ошибки адаптера), не теряя счёт удалённого.
        /// </summary>
        public Exception? Exception { get; }

        public static BatchDeleteOutcome Success(int removedCount)
        {
            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(true, false, removedCount, null, null);
        }

        public static BatchDeleteOutcome Failure(string error, int removedCount = 0, Exception? exception = null)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("Нужно описание ошибки.", nameof(error));
            }

            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(false, false, removedCount, error, exception);
        }

        public static BatchDeleteOutcome Cancelled(int removedCount)
        {
            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(false, true, removedCount, null, null);
        }

        private static void RequireNonNegative(int removedCount)
        {
            if (removedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(removedCount));
            }
        }
    }
}
