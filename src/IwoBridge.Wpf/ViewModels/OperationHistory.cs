using System;
using System.Collections.Generic;
using System.Globalization;
using IwoBridge.App.History;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Запись операций вкладок в журнал — одно место для копирования и очистки (DRY). Итог берётся тем же текстом, что
    /// видит человек в окне: журнал не расходится с экраном.
    /// </summary>
    internal static class OperationHistory
    {
        /// <summary>Период для журнала: «01.10.2026 – 31.12.2026».</summary>
        public static string Period(DateTime? first, DateTime? last) =>
            first.HasValue && last.HasValue
                ? first.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + " – " + last.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                : string.Empty;

        /// <summary>Подробности записи: текст итога по строкам и сообщение об ошибке (с подсказкой и сведениями для администратора).</summary>
        public static IEnumerable<string> Details(string? resultText, UserMessage? error)
        {
            if (!string.IsNullOrWhiteSpace(resultText))
            {
                foreach (string line in resultText!.Split('\n'))
                {
                    if (line.Trim().Length > 0)
                    {
                        yield return line.TrimEnd();
                    }
                }
            }

            if (error != null)
            {
                yield return "Ошибка: " + error.Title;
                if (!string.IsNullOrWhiteSpace(error.Hint))
                {
                    yield return error.Hint;
                }

                if (!string.IsNullOrWhiteSpace(error.Details))
                {
                    yield return "Для администратора: " + error.Details;
                }
            }
        }

        /// <summary>Записать, не роняя операцию: журнал — справка, сбой его записи не должен стать сбоем копирования.</summary>
        public static void Write(IOperationLog? log, Func<OperationRecord> record)
        {
            if (log is null)
            {
                return;
            }

            try
            {
                log.Record(record());
            }
#pragma warning disable CA1031 // Журнал не должен ломать выполненную операцию; причина — в трассировке.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceWarning("Запись журнала операций: " + ex.Message);
            }
        }
    }
}
