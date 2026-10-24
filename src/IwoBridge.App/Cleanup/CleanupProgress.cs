namespace IwoBridge.App.Cleanup
{
    /// <summary>Этап предпросмотра или выполнения очистки — для индикатора хода работы.</summary>
    public enum CleanupStage
    {
        /// <summary>Постраничное чтение календаря; известны прочитанное и общее число элементов.</summary>
        ReadingCalendar,

        /// <summary>Дочитывание данных серий (границы, UID, исключения); число не сообщается.</summary>
        ReadingSeries,

        /// <summary>Раскрытие экземпляров серий в периоде; число не сообщается.</summary>
        ReadingPeriod,

        /// <summary>Запись манифеста удаляемого.</summary>
        WritingManifest,

        /// <summary>Удаление пакетами; известны учтённое и общее число элементов.</summary>
        Deleting,
    }

    /// <summary>
    /// Сообщение о ходе очистки. <see cref="Total"/> == 0 — объём этапа неизвестен (неопределённый индикатор).
    /// Сообщения приходят через <see cref="System.IProgress{T}"/>: при создании <c>Progress&lt;T&gt;</c> в потоке
    /// интерфейса они доставляются в поток интерфейса, и окно можно обновлять напрямую.
    /// </summary>
    public readonly struct CleanupProgress
    {
        public CleanupProgress(CleanupStage stage, int completed = 0, int total = 0)
        {
            Stage = stage;
            Completed = completed;
            Total = total;
        }

        public CleanupStage Stage { get; }

        public int Completed { get; }

        public int Total { get; }

        /// <summary>Объём этапа известен — можно показать «N из M».</summary>
        public bool IsDeterminate => Total > 0;
    }
}
