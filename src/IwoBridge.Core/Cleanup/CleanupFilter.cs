namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Что считать подлежащим удалению.
    /// <see cref="AllEvents"/> — любые события календаря (режим R1: убрать наследие прежнего приложения).
    /// <see cref="BridgeCopiesOnly"/> — только копии, созданные мостом (по метке); режим по умолчанию в R2 (T27).
    /// </summary>
    public enum CleanupFilter
    {
        AllEvents,
        BridgeCopiesOnly,
    }
}
