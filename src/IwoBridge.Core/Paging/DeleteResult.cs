using System;

namespace IwoBridge.Core.Paging
{
    /// <summary>Исход удаления одного элемента (нейтрально к транспорту: без типов EWS).</summary>
    public enum DeleteItemStatus
    {
        /// <summary>
        /// Исход не задан: значение по умолчанию у незаполненного <see cref="DeleteResult"/>. Считается неуспехом —
        /// адаптер, забывший заполнить ответ, не должен выглядеть как «удалено» (отказ в безопасную сторону).
        /// </summary>
        Unknown,

        /// <summary>Элемент удалён.</summary>
        Removed,

        /// <summary>Элемента уже не было — идемпотентный успех (ErrorItemNotFound).</summary>
        AlreadyAbsent,

        /// <summary>Удалить не удалось.</summary>
        Failed,
    }

    /// <summary>
    /// Результат удаления одного элемента. Адаптер EWS переводит ответ сервера в это представление,
    /// а оценку полноты пакета делает <see cref="CompletenessGuard"/> (SoC).
    /// </summary>
    public readonly struct DeleteResult
    {
        private DeleteResult(DeleteItemStatus status, string? errorCode)
        {
            Status = status;
            ErrorCode = errorCode;
        }

        public DeleteItemStatus Status { get; }

        /// <summary>Код ошибки сервера при <see cref="DeleteItemStatus.Failed"/> (иначе <c>null</c>).</summary>
        public string? ErrorCode { get; }

        public static DeleteResult Removed()
        {
            return new DeleteResult(DeleteItemStatus.Removed, null);
        }

        public static DeleteResult AlreadyAbsent()
        {
            return new DeleteResult(DeleteItemStatus.AlreadyAbsent, null);
        }

        public static DeleteResult Failed(string errorCode)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                throw new ArgumentException("Нужен код ошибки.", nameof(errorCode));
            }

            return new DeleteResult(DeleteItemStatus.Failed, errorCode);
        }

        /// <summary>Учтён ли элемент как успешно обработанный (удалён или уже отсутствовал).</summary>
        public bool IsAccountedFor =>
            Status == DeleteItemStatus.Removed || Status == DeleteItemStatus.AlreadyAbsent;
    }
}
