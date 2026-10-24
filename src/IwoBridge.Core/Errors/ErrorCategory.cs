namespace IwoBridge.Core.Errors
{
    /// <summary>Категория ошибки, определяющая политику повторов.</summary>
    public enum ErrorCategory
    {
        /// <summary>Временная: повтор с нарастающей задержкой, ограниченное число раз.</summary>
        Transient,

        /// <summary>Конфликт версий: перечитать элемент и повторить один раз.</summary>
        VersionConflict,

        /// <summary>Аутентификация: повтор запрещён (риск блокировки учётки, T3).</summary>
        Authentication,

        /// <summary>Постоянная: повтор бессмыслен, показать пользователю.</summary>
        Permanent,
    }
}
