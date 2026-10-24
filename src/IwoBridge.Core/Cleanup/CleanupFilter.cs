namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Что считать подлежащим удалению.
    /// <see cref="AllEvents"/> — любые события календаря в выбранном периоде.
    /// <see cref="BridgeCopiesOnly"/> — только копии, созданные программой (по служебной метке).
    /// </summary>
    public enum CleanupFilter
    {
        AllEvents,
        BridgeCopiesOnly,
    }
}
