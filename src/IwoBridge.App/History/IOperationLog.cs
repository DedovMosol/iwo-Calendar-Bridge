using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>Сообщение о ходе работы (начало операции) — для консоли; в журнал не пишется.</summary>
        void Info(string message);

        /// <summary>Последние операции, новые первыми.</summary>
        IReadOnlyList<OperationRecord> Recent(int max);
    }

    /// <summary>Строка журнала для человека (консоль): одно место формата (DRY).</summary>
    public static class OperationRecordText
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

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
            text.Append(record.FinishedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append("  ").Append(KindName(record.Kind))
                .Append("  ").Append(Route(record));
            if (record.Period.Length > 0)
            {
                text.Append("  [").Append(record.Period).Append(']');
            }

            text.Append("  создано ").Append(record.Created)
                .Append(", перезаписано ").Append(record.Updated)
                .Append(", удалено ").Append(record.Deleted)
                .Append(", ошибок ").Append(record.Failed)
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
