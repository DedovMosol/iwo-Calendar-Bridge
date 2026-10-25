using System;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Итог прогона очистки — ровно один из исходов:
    ///  - <see cref="Succeeded"/> — удалено всё, что было и в подтверждённом предпросмотре, и в свежей сверке
    ///    (исчезнувшее или изменившееся после предпросмотра не трогается и считается в
    ///    <see cref="ChangedSincePreviewCount"/>);
    ///  - <see cref="Refused"/> — безопасный отказ до удаления (неполные данные, манифест не записан);
    ///  - <see cref="Cancelled"/> — остановлено пользователем между пакетами; счётчики точные;
    ///  - <see cref="FinishedWithRefusals"/> — пройден весь список, сервер отказал по <see cref="RefusedCount"/> элементам;
    ///  - иначе — ошибка во время удаления, счётчики показывают, что успело выполниться.
    /// Прерванную очистку можно запустить снова: уже удалённое сервер вернёт как «нет такого элемента», и оно
    /// засчитается без повторного удаления. Счётчики учитывают и элементы, которых к моменту удаления уже не было.
    /// </summary>
    public sealed class CleanupResult
    {
        private CleanupResult(
            bool succeeded,
            bool refused,
            bool cancelled,
            string? message,
            int deletedCount,
            int trimmedCount,
            int newSincePreviewCount,
            int changedSincePreviewCount,
            string? manifestPath,
            Exception? exception = null,
            int refusedCount = 0,
            bool finishedWithRefusals = false)
        {
            Exception = exception;
            RefusedCount = refusedCount;
            FinishedWithRefusals = finishedWithRefusals;
            Succeeded = succeeded;
            Refused = refused;
            Cancelled = cancelled;
            Message = message;
            DeletedCount = deletedCount;
            TrimmedCount = trimmedCount;
            NewSincePreviewCount = newSincePreviewCount;
            ChangedSincePreviewCount = changedSincePreviewCount;
            ManifestPath = manifestPath;
        }

        public bool Succeeded { get; }

        public bool Refused { get; }

        /// <summary>Остановлено пользователем; удалённое до остановки отражено в счётчиках.</summary>
        public bool Cancelled { get; }

        /// <summary>Причина отказа или описание ошибки; у успеха и отмены — <c>null</c>.</summary>
        public string? Message { get; }

        /// <summary>Мастера серий и одиночные события, удалённые целиком.</summary>
        public int DeletedCount { get; }

        /// <summary>Экземпляры серий, удалённые при обрезке границы периода.</summary>
        public int TrimmedCount { get; }

        /// <summary>Появились к удалению, но не были в подтверждённом предпросмотре — не тронуты.</summary>
        public int NewSincePreviewCount { get; }

        /// <summary>Были в предпросмотре, но к удалению исчезли или перестали подлежать удалению — не тронуты.</summary>
        public int ChangedSincePreviewCount { get; }

        /// <summary>
        /// Расположение манифеста удаляемого, записанного перед удалением. <c>null</c> — удаление не начиналось
        /// (нечего удалять или отказ).
        /// </summary>
        public string? ManifestPath { get; }

        /// <summary>Исключение адаптера, оборвавшее удаление (для понятной причины в окне); иначе <c>null</c>.</summary>
        public Exception? Exception { get; }

        /// <summary>Сколько элементов сервер отказался удалить (нет прав на элемент, элемент занят).</summary>
        public int RefusedCount { get; }

        /// <summary>
        /// Весь список пройден (и целые события, и экземпляры серий), не удалено только то, от чего сервер отказался
        /// (<see cref="RefusedCount"/>). Не путать с ошибкой: у неё список оборван, а отказы до обрыва тоже в счёте.
        /// </summary>
        public bool FinishedWithRefusals { get; }

        public static CleanupResult Success(
            int deletedCount, int trimmedCount, int newSincePreview, int changedSincePreview, string? manifestPath)
        {
            return new CleanupResult(
                true, false, false, null, deletedCount, trimmedCount, newSincePreview, changedSincePreview, manifestPath);
        }

        public static CleanupResult RefusedResult(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Нужна причина отказа.", nameof(reason));
            }

            return new CleanupResult(false, true, false, reason, 0, 0, 0, 0, null);
        }

        public static CleanupResult CancelledResult(
            int deletedCount, int trimmedCount, int newSincePreview, int changedSincePreview, string manifestPath)
        {
            return new CleanupResult(
                false, false, true, null, deletedCount, trimmedCount, newSincePreview, changedSincePreview, manifestPath);
        }

        public static CleanupResult Failed(
            string message,
            int deletedCount,
            int trimmedCount,
            int newSincePreview,
            int changedSincePreview,
            string? manifestPath,
            Exception? exception = null,
            int refusedCount = 0)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Нужно описание ошибки.", nameof(message));
            }

            if (refusedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(refusedCount));
            }

            return new CleanupResult(
                false, false, false, message, deletedCount, trimmedCount, newSincePreview, changedSincePreview, manifestPath, exception, refusedCount);
        }

        /// <summary>Весь список пройден, сервер не удалил <paramref name="refusedCount"/> элементов.</summary>
        public static CleanupResult WithRefusals(
            string message,
            int deletedCount,
            int trimmedCount,
            int refusedCount,
            int newSincePreview,
            int changedSincePreview,
            string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Нужно описание отказов.", nameof(message));
            }

            if (refusedCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(refusedCount));
            }

            return new CleanupResult(
                false, false, false, message, deletedCount, trimmedCount, newSincePreview, changedSincePreview, manifestPath,
                exception: null, refusedCount: refusedCount, finishedWithRefusals: true);
        }
    }
}
