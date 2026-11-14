using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
                .Select(n => new EventCategory(n, ColorOf(n, colors)))
                .ToArray();
        }

        /// <summary>
        /// Цвет для подсказки в превью: сначала список источника, иначе стандартное имя Outlook (EWS 2007 список
        /// категорий ящика не отдаёт, палитру приёмника не пишем).
        /// </summary>
        public static CategoryColor? ColorOf(string name, IReadOnlyDictionary<string, CategoryColor>? colors = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (colors != null && colors.TryGetValue(name.Trim(), out CategoryColor fromList))
            {
                return fromList;
            }

            return Preset.TryGetValue(name.Trim(), out CategoryColor preset) ? preset : (CategoryColor?)null;
        }

        private static readonly Dictionary<string, CategoryColor> Preset = CreatePreset();

        private static Dictionary<string, CategoryColor> CreatePreset()
        {
            var map = new Dictionary<string, CategoryColor>(StringComparer.OrdinalIgnoreCase);
            void Add(CategoryColor color, params string[] names)
            {
                foreach (string n in names)
                {
                    map[n] = color;
                }
            }

            Add(CategoryColor.Red, "Red", "Red Category", "Красный", "Красная категория");
            Add(CategoryColor.Orange, "Orange", "Orange Category", "Оранжевый", "Оранжевая категория");
            Add(CategoryColor.Yellow, "Yellow", "Yellow Category", "Жёлтый", "Желтый", "Жёлтая категория", "Желтая категория");
            Add(CategoryColor.Green, "Green", "Green Category", "Зелёный", "Зеленый", "Зелёная категория", "Зеленая категория");
            Add(CategoryColor.Blue, "Blue", "Blue Category", "Синий", "Синяя категория");
            Add(CategoryColor.Purple, "Purple", "Purple Category", "Фиолетовый", "Лиловый", "Фиолетовая категория", "Лиловая категория");
            return map;
        }

        /// <summary>Порядок для отпечатка: в нём порядок названий не важен.</summary>
        internal static IEnumerable<string> Ordered(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);

        /// <summary>То же, из байт свойства (UTF-8, UTF-16, с BOM).</summary>
        public static IReadOnlyDictionary<string, CategoryColor> FromListXml(byte[]? data)
        {
            if (data is null || data.Length == 0)
            {
                return FromListXml((string?)null);
            }

            return FromListXml(DecodeXml(data));
        }

        private static string DecodeXml(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            }

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            }

            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            }

            if (data.Length >= 4 && data[0] != 0 && data[1] == 0 && data[3] == 0)
            {
                return Encoding.Unicode.GetString(data);
            }

            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Список категорий ящика (CategoryList.xsd, скрытое сообщение календаря). Цвет в XML на единицу меньше
        /// <see cref="CategoryColor"/> Outlook. Разбор без System.Xml: ядро на него ссылаться не может.
        /// </summary>
        public static IReadOnlyDictionary<string, CategoryColor> FromListXml(string? xml)
        {
            var map = new Dictionary<string, CategoryColor>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(xml))
            {
                return map;
            }

            string text = xml!;
            int cursor = 0;
            while (true)
            {
                int start = IndexOfIgnoreCase(text, "<category", cursor);
                if (start < 0)
                {
                    return map;
                }

                int after = start + 9;
                if (after < text.Length)
                {
                    char next = text[after];
                    if (next != ' ' && next != (char)9 && next != '/' && next != '>')
                    {
                        cursor = after;
                        continue;
                    }
                }

                int end = text.IndexOf('>', start);
                if (end < 0)
                {
                    return map;
                }

                string tag = text.Substring(start, end - start);
                cursor = end + 1;
                string? name = Attribute(tag, "name");
                string? colorText = Attribute(tag, "color");
                if (string.IsNullOrWhiteSpace(name) || colorText is null)
                {
                    continue;
                }

                if (!int.TryParse(colorText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int xmlColor))
                {
                    continue;
                }

                int outlook = xmlColor + 1;
                if (outlook > 0 && Enum.IsDefined(typeof(CategoryColor), outlook))
                {
                    string trimmed = name!.Trim();
                    if (trimmed.Length > 0 && !map.ContainsKey(trimmed))
                    {
                        map.Add(trimmed, (CategoryColor)outlook);
                    }
                }
            }
        }

        private static string? Attribute(string tag, string name)
        {
            const char doubleQuote = (char)34;
            const char singleQuote = (char)39;
            string doublePref = name + "=" + doubleQuote;
            int at = IndexOfIgnoreCase(tag, doublePref);
            if (at >= 0)
            {
                int start = at + doublePref.Length;
                int end = tag.IndexOf(doubleQuote, start);
                return end < 0 ? null : tag.Substring(start, end - start);
            }

            string singlePref = name + "=" + singleQuote;
            at = IndexOfIgnoreCase(tag, singlePref);
            if (at < 0)
            {
                return null;
            }

            int value = at + singlePref.Length;
            int close = tag.IndexOf(singleQuote, value);
            return close < 0 ? null : tag.Substring(value, close - value);
        }

        private static int IndexOfIgnoreCase(string text, string value, int start = 0) =>
            text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
    }
}
