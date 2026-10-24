using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Time;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Пункт списка «Часовой пояс приёмника»: смещение от UTC и подпись. Смещение компьютера пользователя отмечено
    /// «сейчас на этом компьютере» — подсказка, а не скрытое значение по умолчанию: пользователь видит выбор.
    /// </summary>
    public sealed class OffsetOption
    {
        /// <summary>Все смещения действующих часовых поясов мира (в том числе получасовые и с 45 минутами).</summary>
        private static readonly int[] StandardMinutes =
        {
            -720, -660, -600, -570, -540, -480, -420, -360, -300, -240, -210, -180, -120, -60,
            0, 60, 120, 180, 210, 240, 270, 300, 330, 345, 360, 390, 420, 480, 525, 540, 570, 600, 630, 660, 720,
            765, 780, 840,
        };

        public OffsetOption(TimeSpan offset, bool isLocal)
        {
            Offset = offset;
            Label = "UTC" + TargetTimeZone.FormatOffset(offset) + (isLocal ? " — сейчас на этом компьютере" : string.Empty);
        }

        public TimeSpan Offset { get; }

        public string Label { get; }

        /// <summary>
        /// Список для выбора: стандартные смещения плюс <paramref name="extra"/> (значение из настроек, если оно
        /// нестандартное), по возрастанию.
        /// </summary>
        public static IReadOnlyList<OffsetOption> All(TimeSpan localOffset, TimeSpan? extra = null)
        {
            IEnumerable<TimeSpan> offsets = StandardMinutes.Select(m => TimeSpan.FromMinutes(m));
            if (extra.HasValue)
            {
                offsets = offsets.Concat(new[] { extra.Value });
            }

            return offsets
                .Concat(new[] { localOffset })
                .Distinct()
                .OrderBy(o => o)
                .Select(o => new OffsetOption(o, o == localOffset))
                .ToArray();
        }

        public override string ToString() => Label;
    }
}
