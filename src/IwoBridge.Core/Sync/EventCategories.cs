using System;
using System.Collections.Generic;
using System.Linq;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Цвет категории. Значения — как у Outlook (OlCategoryColor): адаптер Outlook передаёт без перевода. В файле списка
    /// категорий почтового ящика (CategoryList.xsd) тот же цвет записан числом на единицу меньше.
    /// </summary>
    public enum CategoryColor
    {
        None = 0,
        Red = 1,
        Orange = 2,
        Peach = 3,
        Yellow = 4,
        Green = 5,
        Teal = 6,
        Olive = 7,
        Blue = 8,
        Purple = 9,
        Maroon = 10,
        Steel = 11,
        DarkSteel = 12,
        Gray = 13,
        DarkGray = 14,
        Black = 15,
        DarkRed = 16,
        DarkOrange = 17,
        DarkPeach = 18,
        DarkYellow = 19,
        DarkGreen = 20,
        DarkTeal = 21,
        DarkOlive = 22,
        DarkBlue = 23,
        DarkPurple = 24,
        DarkMaroon = 25,
    }

    /// <summary>Категория, которая будет у копий, и её цвет в источнике (<c>null</c> — источник цвет не сообщил).</summary>
    public sealed class EventCategory
    {
        public EventCategory(string name, CategoryColor? color)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Color = color;
        }

        public string Name { get; }

        public CategoryColor? Color { get; }
    }

    /// <summary>
    /// Категории события («цветовые категории» Outlook). У события хранятся только названия; цвет задаёт список категорий
    /// почтового ящика, в котором событие показывается. Поэтому копия получает названия, а цвет у неё — из списка
    /// категорий приёмника. Названия сравниваются без учёта регистра, как в Outlook.
    /// </summary>
    public static class EventCategories
    {
        /// <summary>Названия без пустых и повторов (без учёта регистра), обрезанные; порядок источника сохраняется.</summary>
        public static IReadOnlyList<string> Normalize(IEnumerable<string?>? names)
        {
            if (names is null)
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string? name in names)
            {
                string trimmed = (name ?? string.Empty).Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }

            return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
        }

        /// <summary>
        /// Категории изменённого повторения для <see cref="SourceException"/>: <c>null</c> — те же, что у серии (тогда
        /// отпечаток серии не меняется), иначе свои, возможно пустые.
        /// </summary>
        public static IReadOnlyList<string>? OfOccurrence(IEnumerable<string?>? occurrence, IReadOnlyList<string> series)
        {
            IReadOnlyList<string> own = Normalize(occurrence);
            return Same(own, series ?? Array.Empty<string>()) ? null : own;
        }

        /// <summary>Тот же набор названий: порядок не важен, регистр важен (его смена переносится в копию).</summary>
        public static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            a.Count == b.Count && Ordered(a).SequenceEqual(Ordered(b), StringComparer.Ordinal);

        /// <summary>
        /// Категории, которые будут у копий этих событий (вместе со своими категориями изменённых повторений), по алфавиту и
        /// с цветом из списка категорий источника — для подсказки, какие категории завести в приёмнике.
        /// </summary>
        public static IReadOnlyList<EventCategory> Used(IEnumerable<SourceItem> items, IReadOnlyDictionary<string, CategoryColor>? colors)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            IEnumerable<string> names = items.SelectMany(i => i.Categories.Concat(i.Exceptions.SelectMany(e => e.Categories ?? Array.Empty<string>())));
            return Normalize(names)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new EventCategory(n, colors != null && colors.TryGetValue(n, out CategoryColor color) ? color : (CategoryColor?)null))
                .ToArray();
        }

        /// <summary>Порядок для отпечатка: в нём порядок названий не важен.</summary>
        internal static IEnumerable<string> Ordered(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);
    }
}
