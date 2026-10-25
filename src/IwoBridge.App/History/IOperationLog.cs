using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IwoBridge.App.History
{
    /// <summary>
    /// Журнал операций. Запись не должна мешать работе: сбой записи (нет места, нет прав) реализация глотает и сообщает
    /// в трассировку, а не исключением в операцию, которая уже выполнена.
    /// </summary>
    public interface IOperationLog
    {
        /// <summary>Записана новая операция — окно «История» добавляет её сразу.</summary>
        event EventHandler<OperationRecord>? Recorded;

        /// <summary>Где лежит журнал (файл); <c>null</c> — только в памяти (демонстрационный режим).</summary>
        string? Location { get; }

        /// <summary>Записать законченную операцию.</summary>
        void Record(OperationRecord record);

        /// <summary>
        /// Сообщение о ходе работы (начало операции, предпросмотр, Outlook, учётные записи) — для консоли; в журнал не
        /// пишется. <paramref name="isProblem"/> — что-то не получилось: строка выделяется цветом.
        /// </summary>
        void Info(string message, bool isProblem = false);

        /// <summary>Последние операции, новые первыми.</summary>
        IReadOnlyList<OperationRecord> Recent(int max);
    }

    /// <summary>Вид строки консоли — для цвета в панели окна.</summary>
    public enum OperationLineKind
    {
        /// <summary>Ход работы.</summary>
        Info,

        /// <summary>Операция выполнена целиком.</summary>
        Success,

        /// <summary>Что-то не получилось.</summary>
        Problem,

        /// <summary>Операцию остановил человек.</summary>
        Cancelled,

        /// <summary>Подробность к строке выше (причина сбоя по событию).</summary>
        Detail,
    }

    /// <summary>Строка консоли: текст как его видит человек и вид (для цвета).</summary>
    public sealed class OperationLine
    {
        public OperationLine(OperationLineKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }

        public OperationLineKind Kind { get; }

        public string Text { get; }
    }

    /// <summary>Строка журнала для человека (консоль): одно место формата (DRY).</summary>
    public static class OperationRecordText
    {
        /// <summary>Сколько подробностей неуспешной операции выводится в консоль; все — в «Истории».</summary>
        public const int ConsoleDetails = 10;

        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Местное время строки консоли: «2026-10-25 14:30:05».</summary>
        public static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>Строка хода работы: время и сообщение.</summary>
        public static OperationLine InfoLine(DateTime local, string message, bool isProblem) =>
            new OperationLine(isProblem ? OperationLineKind.Problem : OperationLineKind.Info, Stamp(local) + "  " + (message ?? string.Empty));

        /// <summary>
        /// Строки консоли о законченной операции: итог (<see cref="Line"/>) и, если что-то не так, первые
        /// <see cref="ConsoleDetails"/> подробностей с отступом.
        /// </summary>
        public static IReadOnlyList<OperationLine> Lines(OperationRecord record)
        {
            var lines = new List<OperationLine> { new OperationLine(KindOf(record.Outcome), Line(record)) };
            if (record.Outcome != OperationOutcome.Success)
            {
                foreach (string detail in record.Details.Take(ConsoleDetails))
                {
                    lines.Add(new OperationLine(OperationLineKind.Detail, "    " + detail.Replace("\n", "\n    ")));
                }
            }

            return lines;
        }

        private static OperationLineKind KindOf(OperationOutcome outcome) =>
            outcome == OperationOutcome.Success ? OperationLineKind.Success
            : outcome == OperationOutcome.Cancelled ? OperationLineKind.Cancelled
            : OperationLineKind.Problem;

        public static string KindName(OperationKind kind)
        {
            switch (kind)
            {
                case OperationKind.AutoCopy:
                    return "Автокопирование";
                case OperationKind.Cleanup:
                    return "Очистка";
                default:
                    return "Копирование";
            }
        }

        public static string OutcomeName(OperationOutcome outcome)
        {
            switch (outcome)
            {
                case OperationOutcome.Success:
                    return "готово";
                case OperationOutcome.Cancelled:
                    return "остановлено";
                default:
                    return "есть проблемы";
            }
        }

        /// <summary>«12 с», «3 мин 05 с», «1 ч 02 мин».</summary>
        public static string Duration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 60)
            {
                return Math.Max(0, (int)Math.Round(duration.TotalSeconds)).ToString(Russian) + " с";
            }

            if (duration.TotalMinutes < 60)
            {
                return ((int)duration.TotalMinutes).ToString(Russian) + " мин " + duration.Seconds.ToString("00", Russian) + " с";
            }

            return ((int)duration.TotalHours).ToString(Russian) + " ч " + duration.Minutes.ToString("00", Russian) + " мин";
        }

        /// <summary>Маршрут: «Outlook: Календарь → ящик на сервере» или только ящик (очистка).</summary>
        public static string Route(OperationRecord record) =>
            record.Source is null ? record.Target : record.Source + " → " + record.Target;

        /// <summary>
        /// Текст для консоли: типографские знаки заменены на ASCII. Их нет в кодовой странице консоли (866 у русской
        /// Windows) и в её растровом шрифте (по умолчанию в Windows 7): стрелка пропала бы, тире и кавычки стали бы «?».
        /// Кириллица в странице 866 есть.
        /// </summary>
        public static string Plain(string text)
        {
            var result = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\u2192': // →
                        result.Append("->");
                        break;
                    case '\u2014': // —
                    case '\u2013': // –
                    case '\u2212': // −
                        result.Append('-');
                        break;
                    case '\u00AB': // «
                    case '\u00BB': // »
                    case '\u201C': // “
                    case '\u201D': // ”
                    case '\u201E': // „
                        result.Append('"');
                        break;
                    case '\u2018': // ‘
                    case '\u2019': // ’
                        result.Append('\'');
                        break;
                    case '\u2026': // …
                        result.Append("...");
                        break;
                    case '\u00A0': // неразрывный пробел
                    case '\u202F': // узкий неразрывный пробел
                        result.Append(' ');
                        break;
                    case '\u00D7': // ×
                        result.Append('x');
                        break;
                    default:
                        result.Append(c);
                        break;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Одна строка для консоли: местное время окончания, операция, маршрут, период, счётчики, итог, длительность.
        /// </summary>
        public static string Line(OperationRecord record)
        {
            var text = new StringBuilder();
            text.Append(Stamp(record.FinishedUtc.ToLocalTime()))
                .Append("  ").Append(KindName(record.Kind))
                .Append("  ").Append(Route(record));
            if (record.Period.Length > 0)
            {
                text.Append("  [").Append(record.Period).Append(']');
            }

            // Числа — с разделителем разрядов, как в окне («2 441»); в консоли Windows он становится пробелом (Plain).
            text.Append("  создано ").Append(record.Created.ToString("N0", Russian))
                .Append(", перезаписано ").Append(record.Updated.ToString("N0", Russian))
                .Append(", удалено ").Append(record.Deleted.ToString("N0", Russian))
                .Append(", ошибок ").Append(record.Failed.ToString("N0", Russian))
                .Append("  — ").Append(OutcomeName(record.Outcome))
                .Append(" (").Append(Duration(record.Duration)).Append(')');
            if (record.Outcome != OperationOutcome.Success && record.Title.Length > 0)
            {
                text.Append(": ").Append(record.Title);
            }

            return text.ToString();
        }
    }
}
