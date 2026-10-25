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
            string? body = null,
            bool isPrivate = false,
            IEnumerable<string>? categories = null)
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
                IsPrivate = isPrivate;
                Categories = categories is null ? null : EventCategories.Normalize(categories);
            }
        }

        public DateTime OriginalStartUtc { get; }

        public bool IsDeleted { get; }

        public DateTime? StartUtc { get; }

        public DateTime? EndUtc { get; }

        public string? Subject { get; }

        public string? Location { get; }

        public string? Body { get; }

        /// <summary>
        /// Изменённое повторение помечено «Частное» или «Конфиденциальное», хотя серия обычная: правило для личного
        /// применяется к нему отдельно (<see cref="CopyRules"/>).
        /// </summary>
        public bool IsPrivate { get; }

        /// <summary>
        /// Свои категории изменённого повторения; <c>null</c> — те же, что у серии (<see cref="EventCategories.OfOccurrence"/>).
        /// Пустой список — у повторения категории сняты, хотя у серии они есть.
        /// </summary>
        public IReadOnlyList<string>? Categories { get; }

        /// <summary>
        /// Каноническая запись для отпечатка. Текстовые поля — с длиной впереди, отсутствие — отдельной меткой: иначе
        /// «a|b» + «c» и «a» + «b|c» давали бы одну запись, а очищенная тема не отличалась бы от отсутствующей.
        /// Свои категории — последним полем и только если они есть: у повторений без них запись прежняя.
        /// </summary>
        public string Canonical => IsDeleted
            ? OriginalStartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|deleted"
            : string.Join(
                "|",
                OriginalStartUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                StartUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                EndUtc!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                Field(Subject),
                Field(Location),
                Field(Body),
                IsPrivate ? "private" : "normal")
              + (Categories is null
                  ? string.Empty
                  : "|categories:" + Categories.Count.ToString(CultureInfo.InvariantCulture)
                    + string.Concat(EventCategories.Ordered(Categories).Select(c => "|" + Field(c))));

        private static string Field(string? value) =>
            value is null ? "-" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
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
            IEnumerable<SyncAttachment>? attachments = null,
            byte[]? rtfCompressed = null,
            IEnumerable<string>? categories = null)
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
            RtfCompressed = rtfCompressed is null || rtfCompressed.Length == 0 ? null : rtfCompressed;
            Categories = EventCategories.Normalize(categories);
        }

        public string SourceId { get; }

        /// <summary>
        /// Категории события (названия, <see cref="EventCategories"/>). Копия получает те же названия; цвет у неё — из
        /// списка категорий приёмника.
        /// </summary>
        public IReadOnlyList<string> Categories { get; }

        /// <summary>
        /// Описание с оформлением и картинками — в формате свойства MAPI <c>PR_RTF_COMPRESSED</c> (<see cref="MapiRtf"/>);
        /// <c>null</c> — только текст <see cref="Body"/>. Копия получает именно его: так переносятся шрифты, таблицы и
        /// вставленные в описание картинки (в календарях Outlook описание хранится в RTF).
        /// </summary>
#pragma warning disable CA1819 // Байты свойства MAPI передаются как есть, без копирования (могут быть сотни КБ).
        public byte[]? RtfCompressed { get; }
#pragma warning restore CA1819

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
            Sensitivity.ToString(),
            RtfCompressed is null ? null : MapiRtf.Fingerprint(RtfCompressed),
            Categories).Hash;

        /// <summary>Описание для планировщика синхронизации.</summary>
        public SourceEvent ToEvent() => new SourceEvent(SourceId, Kind, StartUtc, SpanEndUtc, ContentHash, Subject);

        /// <summary>То же событие с другими исключениями серии (правило для личных повторений).</summary>
        internal SourceItem WithExceptions(IEnumerable<SourceException> exceptions)
        {
            return new SourceItem(
                SourceId, Subject, Location, Body, StartUtc, EndUtc, IsAllDay, Busy, Sensitivity, Recurrence, SeriesEndUtc, exceptions,
                Attachments, RtfCompressed, Categories);
        }

        /// <summary>
        /// Копия события с другим содержимым (для правил переноса, например скрытия личного): без места, описания,
        /// вложений и категорий — название категории тоже говорит о содержании («Врач», «Собеседование»).
        /// </summary>
        internal SourceItem WithoutDetails(string subject, IEnumerable<SourceException> exceptions)
        {
            return new SourceItem(
                SourceId, subject, string.Empty, string.Empty, StartUtc, EndUtc, IsAllDay, Busy, Sensitivity, Recurrence, SeriesEndUtc, exceptions,
                Array.Empty<SyncAttachment>(), rtfCompressed: null, categories: null);
        }
    }

    /// <summary>
    /// Правила переноса события в копию. Применяются один раз после чтения источника: и отпечаток, и
    /// запись считаются от уже подготовленного события — копия всегда соответствует своему отпечатку.
    /// </summary>
    public static class CopyRules
    {
        /// <summary>Тема копии личного события.</summary>
        public const string PrivateSubject = "Личное";

        /// <summary>Личное событие по правилу по умолчанию — <see cref="PrivateEvents.HideDetails"/>.</summary>
        public static SourceItem Prepare(SourceItem item) => Prepare(item, PrivateEvents.HideDetails)!;

        /// <summary>
        /// Событие для копии по правилу для личных (Private) событий; <c>null</c> — не копировать. По умолчанию в копии
        /// личного только время и занятость — без темы, места, описания, вложений и категорий: коллеги
        /// домена-приёмника увидят «занято», содержимое не покидает источник.
        /// </summary>
        public static SourceItem? Prepare(SourceItem item, PrivateEvents privateEvents)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (privateEvents == PrivateEvents.CopyAll)
            {
                return item;
            }

            // «Конфиденциальное» — строже «личного»: правило то же (содержимое не уходит в чужой домен без решения человека).
            if (item.Sensitivity != ItemSensitivity.Private && item.Sensitivity != ItemSensitivity.Confidential)
            {
                // Обычная серия, но отдельное повторение помечено личным: правило — к этому повторению.
                return item.Exceptions.Any(e => e.IsPrivate)
                    ? item.WithExceptions(item.Exceptions.Select(e => !e.IsPrivate ? e : PrepareException(e, privateEvents)))
                    : item;
            }

            return privateEvents == PrivateEvents.Skip
                ? null
                : item.WithoutDetails(PrivateSubject, item.Exceptions.Select(e => PrepareException(e, PrivateEvents.HideDetails)));
        }

        /// <summary>
        /// Повторение по правилу для личного: «Скрыть» — только время, «Не копировать» — как удалённое. Свои категории
        /// скрытого повторения не переносятся: у него категории серии (у личной серии их нет). Серия обычная — её категории
        /// и так видны на остальных повторениях, а снять их у одного повторения Exchange 2007 не позволяет.
        /// </summary>
        private static SourceException PrepareException(SourceException exception, PrivateEvents privateEvents)
        {
            if (exception.IsDeleted)
            {
                return exception;
            }

            return privateEvents == PrivateEvents.Skip
                ? new SourceException(exception.OriginalStartUtc, true)
                : new SourceException(exception.OriginalStartUtc, false, exception.StartUtc, exception.EndUtc, PrivateSubject, string.Empty, string.Empty, exception.IsPrivate);
        }
    }

    /// <summary>Что делать с личными (Private) событиями источника.</summary>
    public enum PrivateEvents
    {
        /// <summary>Копировать время и занятость, тему — «Личное»; без места, описания, вложений и категорий.</summary>
        HideDetails,

        /// <summary>Копировать полностью (копия тоже личная: в общем доступе коллеги видят только «занято»).</summary>
        CopyAll,

        /// <summary>Не копировать; уже сделанные копии удаляются, как события, которых больше нет.</summary>
        Skip,
    }
}
