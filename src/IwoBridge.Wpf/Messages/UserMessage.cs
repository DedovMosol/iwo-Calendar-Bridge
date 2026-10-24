namespace IwoBridge.Wpf.Messages
{
    /// <summary>
    /// Сообщение для человека: что случилось (<see cref="Title"/>), что делать (<see cref="Hint"/>) и технические
    /// подробности для администратора (<see cref="Details"/>, показываются свёрнутыми).
    /// </summary>
    public sealed class UserMessage
    {
        public UserMessage(string title, string hint, string? details = null)
        {
            Title = title;
            Hint = hint;
            Details = string.IsNullOrWhiteSpace(details) ? null : details;
        }

        public string Title { get; }

        public string Hint { get; }

        public string? Details { get; }

        public bool HasDetails => Details != null;
    }
}
