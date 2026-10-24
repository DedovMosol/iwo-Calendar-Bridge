namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Ход чтения снимка для индикатора: сколько уникальных элементов прочитано в текущем обходе и сколько их
    /// заявил сервер. При повторе обхода (календарь менялся во время чтения) счёт начинается заново — индикатор
    /// честно показывает перечитывание.
    /// </summary>
    public readonly struct SnapshotReadProgress
    {
        public SnapshotReadProgress(int itemsRead, int totalItems)
        {
            ItemsRead = itemsRead;
            TotalItems = totalItems;
        }

        /// <summary>Прочитано уникальных элементов в текущем обходе.</summary>
        public int ItemsRead { get; }

        /// <summary>Всего элементов по данным сервера (может меняться, если календарь меняется).</summary>
        public int TotalItems { get; }
    }
}
