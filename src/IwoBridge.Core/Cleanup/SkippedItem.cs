using System;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Cleanup
{
    /// <summary>Причина, по которой элемент не удаляется (показывается в предпросмотре).</summary>
    public enum SkipReason
    {
        /// <summary>Вне периода очистки.</summary>
        OutsidePeriod,

        /// <summary>Не копия моста (в режиме <see cref="CleanupFilter.BridgeCopiesOnly"/>).</summary>
        NotBridgeCopy,

        /// <summary>Не событие календаря (другой класс элемента) — не трогаем.</summary>
        NonAppointmentClass,

        /// <summary>Серия на границе периода без UID — экземпляры нельзя надёжно сопоставить, обрезки нет.</summary>
        SeriesUidUnavailable,

        /// <summary>UID серии совпадает с UID сохраняемой серии — экземпляры не различить, обрезки нет.</summary>
        AmbiguousSeriesUid,
    }

    /// <summary>Пропущенный элемент и причина пропуска.</summary>
    public sealed class SkippedItem
    {
        public SkippedItem(TargetItemInfo item, SkipReason reason)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            Reason = reason;
        }

        public TargetItemInfo Item { get; }

        public SkipReason Reason { get; }
    }
}
