namespace IwoBridge.Core.Model
{
    /// <summary>Вид элемента календаря приёмника в неразвёрнутом перечислении (без occurrences).</summary>
    public enum ItemKind
    {
        /// <summary>Одиночное событие.</summary>
        Single,

        /// <summary>Мастер повторяющейся серии.</summary>
        RecurringMaster,
    }
}
