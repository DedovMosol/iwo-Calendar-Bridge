namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Строка списка предпросмотра — уже готовый текст: вид, тема, начало и конец в поясе приёмника, причина (для
    /// остающихся). Неизменяемая и без логики, чтобы виртуализированный список листал тысячи строк без задержек.
    /// </summary>
    public sealed class PlanRow
    {
        public PlanRow(string kind, string subject, string start, string end, string reason = "")
        {
            Kind = kind;
            Subject = subject;
            Start = start;
            End = end;
            Reason = reason;
        }

        public string Kind { get; }

        public string Subject { get; }

        public string Start { get; }

        public string End { get; }

        public string Reason { get; }
    }
}
