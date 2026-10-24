using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Собственное событие приёмника — созданное человеком, а не программой (без метки источника). Программа такие
    /// события не изменяет и не удаляет никогда; видит их только затем, чтобы не создать рядом дубль.
    /// </summary>
    public sealed class OwnEvent
    {
        public OwnEvent(string? subject, DateTime startUtc, DateTime endUtc, ItemKind kind)
        {
            Subject = subject ?? string.Empty;
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
            Kind = kind;
        }

        public string Subject { get; }

        /// <summary>Начало (у серии — первого повторения).</summary>
        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        public ItemKind Kind { get; }
    }

    /// <summary>
    /// «У вас уже есть такое же событие»: событие источника, для которого программа собирается создать копию, совпадает с
    /// собственным событием приёмника — тот же вид (одиночное или серия), то же начало и конец, та же тема (без учёта
    /// регистра, лишних пробелов и переводов строк). Такая копия по умолчанию не создаётся — иначе в календаре будет
    /// два одинаковых события; человек может отметить её вручную.
    /// </summary>
    public static class OwnEventMatcher
    {
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Идентификаторы событий из <paramref name="candidates"/>, у которых есть собственный двойник.</summary>
        public static ISet<string> Duplicates(IEnumerable<SourceItem> candidates, IEnumerable<OwnEvent> own)
        {
            if (candidates is null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (own is null)
            {
                throw new ArgumentNullException(nameof(own));
            }

            var keys = new HashSet<string>(own.Select(o => Key(o.Kind, o.StartUtc, o.EndUtc, o.Subject)), StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (SourceItem item in candidates)
            {
                if (keys.Contains(Key(item.Kind, item.StartUtc, item.EndUtc, item.Subject)))
                {
                    result.Add(item.SourceId);
                }
            }

            return result;
        }

        private static string Key(ItemKind kind, DateTime start, DateTime end, string subject) =>
            (kind == ItemKind.RecurringMaster ? "S|" : "E|") + start.Ticks + "|" + end.Ticks + "|"
            + Spaces.Replace(subject, " ").Trim().ToUpperInvariant();
    }
}
