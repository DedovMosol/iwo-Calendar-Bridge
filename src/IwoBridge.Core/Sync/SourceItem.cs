using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>Занятость. Значения — как у Outlook (olBusyStatus): адаптер Outlook передаёт без перевода.</summary>
    public enum FreeBusy
    {
        Free = 0,
        Tentative = 1,
        Busy = 2,
        OutOfOffice = 3,
    }

    /// <summary>Конфиденциальность. Значения — как у Outlook (olSensitivity).</summary>
    public enum ItemSensitivity
    {
        Normal = 0,
        Personal = 1,
        Private = 2,
        Confidential = 3,
    }

    /// <summary>
    /// Исключение серии: удалённое повторение или изменённое (другое время, тема, место, описание). Повторение
    /// опознаётся по исходному началу (<see cref="OriginalStartUtc"/>) — так его находит и Outlook, и EWS.
    /// </summary>
    public sealed class SourceException
    {
        public SourceException(
            DateTime originalStartUtc,
            bool isDeleted,
            DateTime? startUtc = null,
            DateTime? endUtc = null,
            string? subject = null,
            string? location = null,
            string? body = null)
        {
            OriginalStartUtc = TimeMapper.ToUtc(originalStartUtc);
            IsDeleted = isDeleted;
            if (!isDeleted)
            {
                if (startUtc is null || endUtc is null)
                {
                    throw new ArgumentException("У изменённого повторения нужны начало и конец.", nameof(startUtc));
                }

                StartUtc = TimeMapper.ToUtc(startUtc.Value);
                EndUtc = TimeMapper.ToUtc(endUtc.Value);
                Subject = subject;
                Location = location;
                Body = body;
            }
        }

        public DateTime OriginalStartUtc { get; }

        public bool IsDeleted { get; }

        public DateTime? StartUtc { get; }

        public DateTime? EndUtc { get; }

        public string? Subject { get; }

        public string? Location { get; }

        public string? Body { get; }

        /// <summary>Каноническая запись для отпечатка.</summary>
        public string Canonical => IsDeleted
            ? OriginalStartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|deleted"
            : string.Join(
                "|",
                OriginalStartUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                StartUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                EndUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                Subject ?? string.Empty,
                Location ?? string.Empty,
                Body ?? string.Empty);
    }

    /// <summary>
    /// Событие исходного календаря со всем, что переносится в копию. Источник — Outlook этого компьютера или сервер по
    /// EWS; модель одна, поэтому планировщик, отпечаток и запись не зависят от источника (SoC).
    /// Серия — одно событие: начало и конец первого повторения, правило и исключения.
    /// </summary>
    public sealed class SourceItem
    {
        /// <param name="sourceId">
        /// Устойчивый идентификатор в источнике: глобальный идентификатор встречи (GlobalAppointmentID / UID). Он же
        /// записывается в копию и по нему копия находится при следующем копировании.
        /// </param>
        /// <param name="seriesEndUtc">
        /// Конец последнего повторения серии; <c>null</c> у серии — бессрочная. У одиночного события не задаётся.
        /// </param>
        public SourceItem(
            string sourceId,
            string? subject,
            string? location,
            string? body,
            DateTime startUtc,
            DateTime endUtc,
            bool isAllDay = false,
            FreeBusy busy = FreeBusy.Busy,
            ItemSensitivity sensitivity = ItemSensitivity.Normal,
            RecurrenceRule? recurrence = null,
            DateTime? seriesEndUtc = null,
            IEnumerable<SourceException>? exceptions = null,
            IEnumerable<SyncAttachment>? attachments = null)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                throw new ArgumentException("Нужен идентификатор события источника.", nameof(sourceId));
            }

            SourceId = sourceId;
            Subject = subject ?? string.Empty;
            Location = location ?? string.Empty;
            Body = body ?? string.Empty;
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
            if (EndUtc < StartUtc)
            {
                throw new ArgumentException("Конец события раньше начала.", nameof(endUtc));
            }

            IsAllDay = isAllDay;
            Busy = busy;
            Sensitivity = sensitivity;
            Recurrence = recurrence;
            SeriesEndUtc = recurrence is null ? null : seriesEndUtc.HasValue ? TimeMapper.ToUtc(seriesEndUtc.Value) : (DateTime?)null;
            Exceptions = recurrence is null ? Array.Empty<SourceException>() : (exceptions ?? Array.Empty<SourceException>()).ToArray();
            Attachments = (attachments ?? Array.Empty<SyncAttachment>()).ToArray();
        }

        public string SourceId { get; }

        public string Subject { get; }

        public string Location { get; }

        public string Body { get; }

        /// <summary>Начало события (у серии — первого повторения).</summary>
        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        public bool IsAllDay { get; }

        public FreeBusy Busy { get; }

        public ItemSensitivity Sensitivity { get; }

        /// <summary>Правило серии; <c>null</c> — одиночное событие.</summary>
        public RecurrenceRule? Recurrence { get; }

        public DateTime? SeriesEndUtc { get; }

        public IReadOnlyList<SourceException> Exceptions { get; }

        public IReadOnlyList<SyncAttachment> Attachments { get; }

        public ItemKind Kind => Recurrence is null ? ItemKind.Single : ItemKind.RecurringMaster;

        /// <summary>Охват: у события — от начала до конца, у серии — от первого повторения до последнего (или бессрочно).</summary>
        public DateTime SpanEndUtc => Recurrence is null ? EndUtc : SeriesEndUtc ?? DateTime.MaxValue;

        /// <summary>Отпечаток всего, что переносится в копию.</summary>
        public string ContentHash => new SyncContent(
            Subject,
            Location,
            Body,
            StartUtc,
            EndUtc,
            IsAllDay,
            Busy.ToString(),
            Recurrence?.Canonical,
            Exceptions.Select(e => e.Canonical),
            Attachments,
            Sensitivity.ToString()).Hash;

        /// <summary>Описание для планировщика синхронизации.</summary>
        public SourceEvent ToEvent() => new SourceEvent(SourceId, Kind, StartUtc, SpanEndUtc, ContentHash, Subject);

        /// <summary>Копия события с другим содержимым (для правил переноса, например скрытия личного).</summary>
        internal SourceItem With(string subject, string location, string body, IEnumerable<SourceException> exceptions, IEnumerable<SyncAttachment> attachments)
        {
            return new SourceItem(
                SourceId, subject, location, body, StartUtc, EndUtc, IsAllDay, Busy, Sensitivity, Recurrence, SeriesEndUtc, exceptions, attachments);
        }
    }

    /// <summary>
    /// Правила переноса события в копию (PLAN.md §6). Применяются один раз после чтения источника: и отпечаток, и
    /// запись считаются от уже подготовленного события — копия всегда соответствует своему отпечатку.
    /// </summary>
    public static class CopyRules
    {
        /// <summary>Тема копии личного события.</summary>
        public const string PrivateSubject = "Личное";

        /// <summary>
        /// Личное (Private) событие: в копии только время и занятость — без темы, места, описания и вложений
        /// (PLAN.md §6.3). Коллеги домена-приёмника увидят «занято», содержимое не покидает источник.
        /// </summary>
        public static SourceItem Prepare(SourceItem item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.Sensitivity != ItemSensitivity.Private)
            {
                return item;
            }

            IEnumerable<SourceException> exceptions = item.Exceptions.Select(e => e.IsDeleted
                ? e
                : new SourceException(e.OriginalStartUtc, false, e.StartUtc, e.EndUtc));
            return item.With(PrivateSubject, string.Empty, string.Empty, exceptions, Array.Empty<SyncAttachment>());
        }
    }
}
