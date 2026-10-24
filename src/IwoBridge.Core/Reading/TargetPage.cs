using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Одна страница постраничного перечисления календаря приёмника. Адаптер EWS заполняет её из
    /// ответа <c>FindItem</c> (неразвёрнутого), переводя элементы в <see cref="TargetItemInfo"/>.
    /// </summary>
    public sealed class TargetPage
    {
        public TargetPage(IReadOnlyList<TargetItemInfo> items, int totalCount, bool includesLastItemInRange)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (totalCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalCount));
            }

            Items = items.ToArray();
            TotalCount = totalCount;
            IncludesLastItemInRange = includesLastItemInRange;
        }

        public IReadOnlyList<TargetItemInfo> Items { get; }

        /// <summary>Заявленное сервером общее число элементов в наборе (TotalItemsInView).</summary>
        public int TotalCount { get; }

        /// <summary>Пометил ли сервер эту страницу последней (больше страниц нет).</summary>
        public bool IncludesLastItemInRange { get; }
    }
}
