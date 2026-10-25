using System;

namespace IwoBridge.Core.Paging
{
    /// <summary>
    /// Итог пакетного удаления. Успех — только если учтены все запрошенные id (удалено либо уже
    /// отсутствовало). Частичный результат — ошибка, не маскируется; при этом
    /// <see cref="RemovedCount"/> сообщает, сколько элементов было учтено до остановки.
    /// Остановка по просьбе пользователя — отдельный исход <see cref="IsCancelled"/>: не ошибка, но и не успех;
    /// <see cref="RemovedCount"/> — точное число удалённого до остановки.
    /// Сервер отказал по отдельным элементам, но все пакеты отправлены — <see cref="IsFinishedWithRefusals"/>: список
    /// пройден до конца, не удалено ровно <see cref="RefusedCount"/>.
    /// </summary>
    public readonly struct BatchDeleteOutcome
    {
        private BatchDeleteOutcome(bool isSuccess, bool isCancelled, int removedCount, int refusedCount, string? error, Exception? exception)
        {
            IsSuccess = isSuccess;
            IsCancelled = isCancelled;
            RemovedCount = removedCount;
            RefusedCount = refusedCount;
            Error = error;
            Exception = exception;
        }

        public bool IsSuccess { get; }

        /// <summary>Остановлено по отмене между пакетами; оставшиеся пакеты не отправлялись.</summary>
        public bool IsCancelled { get; }

        /// <summary>Сколько элементов удалено или уже отсутствовало (для ошибки и отмены — учтено до остановки).</summary>
        public int RemovedCount { get; }

        /// <summary>Сколько элементов сервер отказался удалить, когда список пройден до конца; иначе 0.</summary>
        public int RefusedCount { get; }

        /// <summary>Все пакеты отправлены, но часть элементов сервер не удалил (нет прав на элемент, элемент занят).</summary>
        public bool IsFinishedWithRefusals => RefusedCount > 0;

        /// <summary>Список пройден до конца — успехом или с отказами по элементам; следующую фазу можно выполнять.</summary>
        public bool ReachedEnd => IsSuccess || IsFinishedWithRefusals;

        public string? Error { get; }

        /// <summary>
        /// Исключение порта удаления (обрыв сети, отказ в доступе), если пакет оборвался им. Сохраняется, чтобы
        /// приложение показало понятную причину (по виду ошибки адаптера), не теряя счёт удалённого.
        /// </summary>
        public Exception? Exception { get; }

        public static BatchDeleteOutcome Success(int removedCount)
        {
            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(true, false, removedCount, 0, null, null);
        }

        public static BatchDeleteOutcome Failure(string error, int removedCount = 0, Exception? exception = null)
        {
            RequireError(error);
            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(false, false, removedCount, 0, error, exception);
        }

        /// <summary>Все пакеты отправлены, сервер не удалил <paramref name="refusedCount"/> элементов.</summary>
        public static BatchDeleteOutcome FinishedWithRefusals(string error, int removedCount, int refusedCount)
        {
            RequireError(error);
            RequireNonNegative(removedCount);
            if (refusedCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(refusedCount));
            }

            return new BatchDeleteOutcome(false, false, removedCount, refusedCount, error, null);
        }

        public static BatchDeleteOutcome Cancelled(int removedCount)
        {
            RequireNonNegative(removedCount);
            return new BatchDeleteOutcome(false, true, removedCount, 0, null, null);
        }

        private static void RequireError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("Нужно описание ошибки.", nameof(error));
            }
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
