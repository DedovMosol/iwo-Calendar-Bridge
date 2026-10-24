using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IwoBridge.Core.Sync
{
    /// <summary>Итог чтения источника за период.</summary>
    public sealed class SourceRead
    {
        /// <param name="incompleteReason">Почему чтение неполное (часть элементов не прочиталась); <c>null</c> — полное.</param>
        /// <param name="warnings">
        /// Что прочитано не совсем точно, но чтение от этого не стало неполным — например, повторение серии, которое
        /// источник не открывает: оно в копию не попадает. Показывается человеку в предпросмотре.
        /// </param>
        public SourceRead(IEnumerable<SourceItem> items, string? incompleteReason = null, IEnumerable<string>? warnings = null)
        {
            Items = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
            IncompleteReason = incompleteReason;
            Warnings = (warnings ?? Array.Empty<string>()).ToArray();
        }

        public IReadOnlyList<string> Warnings { get; }

        public IReadOnlyList<SourceItem> Items { get; }

        public string? IncompleteReason { get; }

        public bool IsComplete => IncompleteReason is null;
    }

    /// <summary>
    /// Исходный календарь — только чтение. Реализации: Outlook этого компьютера, сервер Exchange по EWS, память (тесты и
    /// демонстрационный режим).
    /// </summary>
    public interface ISourceCalendar
    {
        /// <summary>Как источник называется человеку («Календарь — ящик в Outlook», «сервер …»).</summary>
        string Label { get; }

        /// <summary>
        /// События, пересекающие период, — плюс ещё существующие события из <paramref name="referencedSourceIds"/>, где бы
        /// они ни были (контракт <see cref="SourceSnapshot"/>: событие, перенесённое за пределы периода, не должно
        /// выглядеть удалённым). Вложения — только описания (имя, размер).
        /// </summary>
        Task<SourceRead> ReadAsync(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            IReadOnlyCollection<string> referencedSourceIds,
            IProgress<int>? progress,
            CancellationToken cancellationToken);

        /// <summary>Содержимое вложений события — только для того, что действительно копируется.</summary>
        Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Календарь-приёмник со стороны копирования: копии моста (опознаются по метке источника) и запись. Чужие события
    /// через этот порт не видны и не меняются.
    /// </summary>
    public interface ICopyTarget
    {
        /// <summary>Все копии моста в календаре — с меткой источника, отпечатком и охватом.</summary>
        Task<IReadOnlyList<BridgeCopy>> ReadCopiesAsync(CancellationToken cancellationToken);

        /// <summary>Создать копию события (без приглашений участникам).</summary>
        Task CreateAsync(SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken);

        /// <summary>Сделать существующую копию равной событию; реализация может пересоздать её (серии).</summary>
        Task UpdateAsync(string copyItemId, SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken);

        /// <summary>Удалить копию (в «Удалённые», без отмен участникам).</summary>
        Task DeleteAsync(string copyItemId, CancellationToken cancellationToken);
    }
}
