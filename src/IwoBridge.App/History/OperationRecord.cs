using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Time;

namespace IwoBridge.App.History
{
    /// <summary>Какая операция записана в журнал.</summary>
    public enum OperationKind
    {
        /// <summary>Копирование, запущенное человеком.</summary>
        Copy,

        /// <summary>Копирование по расписанию.</summary>
        AutoCopy,

        /// <summary>Удаление событий на вкладке «Очистка».</summary>
        Cleanup,
    }

    /// <summary>Чем закончилась операция.</summary>
    public enum OperationOutcome
    {
        /// <summary>Выполнено целиком.</summary>
        Success,

        /// <summary>Выполнено не всё: сбои по событиям, ошибка подключения, отказ (массовое удаление) и т. п.</summary>
        Problem,

        /// <summary>Остановлено человеком.</summary>
        Cancelled,
    }

    /// <summary>
    /// Запись журнала операций: что, когда, сколько и чем закончилось. Только сведения об операции: пароли не
    /// записываются, темы событий — только в строках сбоев (как в окне итога), чтобы было видно, что не удалось.
    /// </summary>
    public sealed class OperationRecord
    {
        /// <summary>Сколько строк подробностей хранится в записи: журнал остаётся небольшим и при тысяче сбоев.</summary>
        public const int MaxDetails = 50;

        public OperationRecord(
            OperationKind kind,
            DateTime startedUtc,
            DateTime finishedUtc,
            OperationOutcome outcome,
            string? source,
            string target,
            string period,
            int created,
            int updated,
            int deleted,
            int failed,
            string title,
            IEnumerable<string>? details = null)
        {
            Kind = kind;
            StartedUtc = TimeMapper.ToUtc(startedUtc);
            FinishedUtc = TimeMapper.ToUtc(finishedUtc) < StartedUtc ? StartedUtc : TimeMapper.ToUtc(finishedUtc);
            Outcome = outcome;
            Source = string.IsNullOrWhiteSpace(source) ? null : source;
            Target = target ?? string.Empty;
            Period = period ?? string.Empty;
            Created = Math.Max(0, created);
            Updated = Math.Max(0, updated);
            Deleted = Math.Max(0, deleted);
            Failed = Math.Max(0, failed);
            Title = title ?? string.Empty;
            Details = (details ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Take(MaxDetails).ToArray();
        }

        public OperationKind Kind { get; }

        public DateTime StartedUtc { get; }

        public DateTime FinishedUtc { get; }

        public TimeSpan Duration => FinishedUtc - StartedUtc;

        public OperationOutcome Outcome { get; }

        /// <summary>Откуда (для копирования); <c>null</c> — у очистки источника нет.</summary>
        public string? Source { get; }

        /// <summary>Куда копировали или какой ящик очищали.</summary>
        public string Target { get; }

        /// <summary>Период как его видел человек: «01.10.2026 – 31.12.2026» или «весь календарь».</summary>
        public string Period { get; }

        public int Created { get; }

        public int Updated { get; }

        public int Deleted { get; }

        public int Failed { get; }

        /// <summary>Итог одной строкой — тот же, что в окне программы.</summary>
        public string Title { get; }

        /// <summary>Подробности: итог целиком, причины сбоев, сведения для администратора.</summary>
        public IReadOnlyList<string> Details { get; }
    }
}
