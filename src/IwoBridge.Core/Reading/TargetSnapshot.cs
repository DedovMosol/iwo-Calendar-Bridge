using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Снимок календаря приёмника: собранные элементы и вердикт о полноте. Разрушительные операции
    /// (удаление) допустимы только при <see cref="IsAuthoritative"/> == true (T2, T30, T32).
    /// </summary>
    public sealed class TargetSnapshot
    {
        public TargetSnapshot(IReadOnlyList<TargetItemInfo> items, Completeness completeness)
        {
            Items = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
            Completeness = completeness;
        }

        public IReadOnlyList<TargetItemInfo> Items { get; }

        public Completeness Completeness { get; }

        /// <summary>Снимок полон и пригоден для разрушительных решений.</summary>
        public bool IsAuthoritative => Completeness.IsComplete;
    }
}
