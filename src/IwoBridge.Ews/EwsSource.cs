using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Collections;
using IwoBridge.Core.Reading;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Ews.Mapping;
using TimeWindow = IwoBridge.Core.Time.TimeWindow;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Исходный календарь на сервере Exchange по EWS — только чтение. Идентификатор события — iCalendar UID (у события,
    /// созданного в Outlook, это тот же глобальный идентификатор встречи, что видит и источник «Outlook»).
    ///
    /// Чтение в два шага, как у очистки: сначала лёгкий перечень всех событий (без раскрытия серий) и границы серий —
    /// чтобы отобрать пересекающие период и запрошенные; затем полные данные только отобранных (описание, правило,
    /// исключения, имена вложений) — пачками, вместе с изменёнными повторениями пачки одним запросом. Содержимое
    /// вложений — по запросу, только для того, что копируется, одним запросом на событие.
    /// Элемент, который не прочитался, делает чтение неполным: по неполному чтению копии не удаляются.
    /// </summary>
    public sealed class EwsSource : ISourceCalendar
    {
        private const int BindBatch = 50;

        private static readonly SearchFilter NotBridgeCopy = new SearchFilter.Not(new SearchFilter.Exists(BridgeExtendedProperties.SourceId));

        private static readonly PropertySet BriefProperties = new PropertySet(
            BasePropertySet.IdOnly, AppointmentSchema.Start, AppointmentSchema.End, AppointmentSchema.AppointmentType, AppointmentSchema.ICalUid);

        private static readonly PropertySet FullProperties = new PropertySet(
            BasePropertySet.IdOnly,
            ItemSchema.Subject,
            ItemSchema.Body,
            ItemSchema.Attachments,
            ItemSchema.Sensitivity,
            AppointmentSchema.Location,
            AppointmentSchema.Start,
            AppointmentSchema.End,
            AppointmentSchema.IsAllDayEvent,
            AppointmentSchema.LegacyFreeBusyStatus,
            AppointmentSchema.Recurrence,
            AppointmentSchema.ModifiedOccurrences,
            AppointmentSchema.DeletedOccurrences,
            MapiProperties.RtfCompressed)
        {
            RequestedBodyType = BodyType.Text,
        };

        private static readonly PropertySet OccurrenceProperties = new PropertySet(
            BasePropertySet.IdOnly, ItemSchema.Subject, ItemSchema.Body, ItemSchema.Sensitivity, AppointmentSchema.Location)
        {
            RequestedBodyType = BodyType.Text,
        };

        private readonly EwsChannel _channel;
        private readonly FolderId _calendar;
        private readonly EwsCalendarGateway _gateway;
        private readonly SearchFilter? _filter;
        private readonly object _sync = new object();
        private Dictionary<string, string> _itemIdBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="includeBridgeCopies">Читать и копии моста — только для проверки записанного (живые тесты).</param>
        internal EwsSource(string label, EwsChannel channel, FolderId calendar, EwsCalendarGateway gateway, bool includeBridgeCopies = false)
        {
            Label = label;
            _channel = channel;
            _calendar = calendar;
            _gateway = gateway;

            // Копии, сделанные этой программой (источник сам чей-то приёмник), не читаются: при встречной настройке
            // иначе копировались бы копии копий.
            _filter = includeBridgeCopies ? null : NotBridgeCopy;
        }

        public string Label { get; }

        public async Task<SourceRead> ReadAsync(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            IReadOnlyCollection<string> referencedSourceIds,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var referenced = new HashSet<string>(referencedSourceIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            (List<Brief> all, bool listed) = await ListAsync(cancellationToken).ConfigureAwait(false);
            string[] masters = all.Where(b => b.IsMaster).Select(b => b.ItemId).ToArray();
            IReadOnlyDictionary<string, RecurrenceInfo> series = await _gateway.LoadAsync(masters, cancellationToken).ConfigureAwait(false);

            Brief[] chosen = all.Where(b =>
            {
                if (referenced.Contains(b.SourceId))
                {
                    return true;
                }

                DateTime spanEnd = b.End;
                if (b.IsMaster)
                {
                    spanEnd = series.TryGetValue(b.ItemId, out RecurrenceInfo? info) && info.LastStartUtc.HasValue
                        ? info.LastStartUtc.Value + (b.End - b.Start)
                        : DateTime.MaxValue;
                }

                return TimeWindow.Overlaps(b.Start, spanEnd, windowStartUtc, windowEndUtc);
            }).ToArray();

            var items = new List<SourceItem>(chosen.Length);
            var warnings = new List<string>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            int unreadable = 0;
            foreach (IReadOnlyList<Brief> batch in Batching.Chunk(chosen, BindBatch))
            {
                IReadOnlyList<Mapped?> read = await ReadFullAsync(batch, series, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < batch.Count; i++)
                {
                    if (read[i] is Mapped mapped)
                    {
                        items.Add(mapped.Item);
                        ids[mapped.Item.SourceId] = batch[i].ItemId;
                        if (mapped.Warning != null)
                        {
                            warnings.Add(mapped.Warning);
                        }
                    }
                    else
                    {
                        unreadable++;
                    }
                }

                progress?.Report(items.Count);
            }

            lock (_sync)
            {
                _itemIdBySource = ids;
            }

            string? incomplete = !listed
                ? "Календарь источника менялся во время чтения — часть событий могла не попасть в перечень."
                : unreadable == 0 ? null : "Не прочитано событий источника: " + unreadable + ".";
            return new SourceRead(items, incomplete, warnings);
        }

        public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            string? itemId;
            lock (_sync)
            {
                _itemIdBySource.TryGetValue(item.SourceId, out itemId);
            }

            if (itemId is null)
            {
                throw new InvalidOperationException("Событие источника не найдено среди прочитанных — постройте предпросмотр заново.");
            }

            return _channel.RunAsync<IReadOnlyList<AttachmentContent>>(
                service =>
                {
                    Item source = Item.Bind(service, new ItemId(itemId), new PropertySet(BasePropertySet.IdOnly, ItemSchema.Attachments));
                    FileAttachment[] files = source.Attachments.OfType<FileAttachment>().ToArray();
                    if (files.Length == 0)
                    {
                        return Array.Empty<AttachmentContent>();
                    }

                    // Все файлы события — одним запросом GetAttachment (Exchange 2007 SP1+), а не запросом на файл.
                    ServiceResponseCollection<GetAttachmentResponse> responses = service.GetAttachments(
                        files.Cast<Attachment>().ToArray(), null, Array.Empty<PropertyDefinitionBase>());
                    var list = new List<AttachmentContent>(files.Length);
                    foreach (GetAttachmentResponse response in responses)
                    {
                        if (response.Result != ServiceResult.Success || !(response.Attachment is FileAttachment file) || file.Content is null)
                        {
                            throw new InvalidOperationException(
                                "Вложение события источника не прочиталось: " + (response.ErrorMessage ?? response.ErrorCode.ToString()) + ".");
                        }

                        list.Add(new AttachmentContent(file.Name, file.Content, file.ContentId));
                    }

                    return list;
                },
                cancellationToken);
        }

        private async Task<(List<Brief> Items, bool Complete)> ListAsync(CancellationToken cancellationToken)
        {
            (IReadOnlyList<Item> found, bool complete) = await EwsPaging
                .FindAllAsync(_channel, _calendar, _filter, BriefProperties, cancellationToken)
                .ConfigureAwait(false);
            var all = new List<Brief>(found.Count);
            foreach (Appointment a in found.OfType<Appointment>())
            {
                if (a.TryGetProperty(AppointmentSchema.Start, out DateTime start) && a.TryGetProperty(AppointmentSchema.End, out DateTime end))
                {
                    a.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type);
                    string sourceId = a.TryGetProperty(AppointmentSchema.ICalUid, out string uid) && !string.IsNullOrEmpty(uid) ? uid : a.Id.UniqueId;
                    all.Add(new Brief(a.Id.UniqueId, sourceId, type == AppointmentType.RecurringMaster, TimeMapper.ToUtc(start), TimeMapper.ToUtc(end)));
                }
            }

            return (all, complete);
        }

        /// <summary>
        /// Полные данные пачки: события одним запросом, затем все изменённые повторения пачки — тоже одним (а не запросом
        /// на повторение). Сбой запроса целиком повторяет канал; элемент, который сервер не отдал, — неполное чтение.
        /// </summary>
        private Task<IReadOnlyList<Mapped?>> ReadFullAsync(
            IReadOnlyList<Brief> batch, IReadOnlyDictionary<string, RecurrenceInfo> series, CancellationToken cancellationToken)
        {
            return _channel.RunAsync<IReadOnlyList<Mapped?>>(
                service =>
                {
                    ServiceResponseCollection<GetItemResponse> responses = service.BindToItems(batch.Select(b => new ItemId(b.ItemId)), FullProperties);
                    var appointments = new Appointment?[batch.Count];
                    for (int i = 0; i < batch.Count && i < responses.Count; i++)
                    {
                        appointments[i] = responses[i].Result == ServiceResult.Success ? responses[i].Item as Appointment : null;
                    }

                    IReadOnlyDictionary<string, Appointment> occurrences = BindModifiedOccurrences(service, batch, appointments);
                    var result = new Mapped?[batch.Count];
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (appointments[i] is Appointment appointment)
                        {
                            try
                            {
                                result[i] = Map(batch[i], appointment, series, occurrences);
                            }
#pragma warning disable CA1031 // Непереносимое событие делает чтение неполным, но не обрывает его.
                            catch (Exception)
#pragma warning restore CA1031
                            {
                                result[i] = null;
                            }
                        }
                    }

                    return result;
                },
                cancellationToken);
        }

        private static IReadOnlyDictionary<string, Appointment> BindModifiedOccurrences(
            ExchangeService service, IReadOnlyList<Brief> batch, IReadOnlyList<Appointment?> appointments)
        {
            var ids = new List<ItemId>();
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i].IsMaster && appointments[i] is Appointment master
                    && master.TryGetProperty(AppointmentSchema.ModifiedOccurrences, out OccurrenceInfoCollection modified) && modified != null)
                {
                    ids.AddRange(modified.Select(o => o.ItemId));
                }
            }

            var found = new Dictionary<string, Appointment>(StringComparer.Ordinal);
            foreach (IReadOnlyList<ItemId> chunk in Batching.Chunk(ids, BindBatch))
            {
                foreach (GetItemResponse response in service.BindToItems(chunk, OccurrenceProperties))
                {
                    if (response.Result == ServiceResult.Success && response.Item is Appointment occurrence)
                    {
                        found[occurrence.Id.UniqueId] = occurrence;
                    }
                }
            }

            return found;
        }

        private static Mapped Map(
            Brief brief, Appointment a, IReadOnlyDictionary<string, RecurrenceInfo> series, IReadOnlyDictionary<string, Appointment> occurrences)
        {
            a.TryGetProperty(ItemSchema.Subject, out string subject);
            a.TryGetProperty(AppointmentSchema.Location, out string location);
            string body = a.TryGetProperty(ItemSchema.Body, out MessageBody messageBody) ? messageBody?.Text ?? string.Empty : string.Empty;
            a.TryGetProperty(AppointmentSchema.IsAllDayEvent, out bool allDay);
            a.TryGetProperty(AppointmentSchema.LegacyFreeBusyStatus, out LegacyFreeBusyStatus busy);
            a.TryGetProperty(ItemSchema.Sensitivity, out Sensitivity sensitivity);
            var attachments = new List<SyncAttachment>();
            string? warning = null;
            if (a.TryGetProperty(ItemSchema.Attachments, out AttachmentCollection files) && files != null)
            {
                // Размер вложения EWS отдаёт начиная с Exchange 2010; для отпечатка хватает имени.
                attachments.AddRange(files.OfType<FileAttachment>().Select(f => new SyncAttachment(f.Name, 0)));
                int embedded = files.OfType<ItemAttachment>().Count();
                if (embedded > 0)
                {
                    warning = "«" + subject + "»: вложенные письма и события (" + embedded + ") в копию не переносятся, только файлы.";
                }
            }

            RecurrenceRule? rule = null;
            DateTime? seriesEnd = null;
            var exceptions = new List<SourceException>();
            if (brief.IsMaster && a.TryGetProperty(AppointmentSchema.Recurrence, out Recurrence recurrence) && recurrence != null)
            {
                rule = EwsRecurrenceMapper.FromEws(recurrence);
                if (series.TryGetValue(brief.ItemId, out RecurrenceInfo? info) && info.LastStartUtc.HasValue && recurrence.HasEnd)
                {
                    seriesEnd = info.LastStartUtc.Value + (brief.End - brief.Start);
                }

                if (a.TryGetProperty(AppointmentSchema.DeletedOccurrences, out DeletedOccurrenceInfoCollection deleted) && deleted != null)
                {
                    exceptions.AddRange(deleted.Select(d => new SourceException(TimeMapper.ToUtc(d.OriginalStart), true)));
                }

                if (a.TryGetProperty(AppointmentSchema.ModifiedOccurrences, out OccurrenceInfoCollection modified) && modified != null)
                {
                    foreach (OccurrenceInfo occurrence in modified)
                    {
                        // Повторение, которого сервер не отдал, — событие не прочитано целиком (а не серия без исключения).
                        if (!occurrences.TryGetValue(occurrence.ItemId.UniqueId, out Appointment? changed))
                        {
                            throw new InvalidOperationException("Изменённое повторение серии не прочиталось.");
                        }

                        changed.TryGetProperty(ItemSchema.Subject, out string s);
                        changed.TryGetProperty(AppointmentSchema.Location, out string l);
                        changed.TryGetProperty(ItemSchema.Sensitivity, out Sensitivity occurrenceSensitivity);
                        string b = changed.TryGetProperty(ItemSchema.Body, out MessageBody mb) ? mb?.Text ?? string.Empty : string.Empty;
                        exceptions.Add(new SourceException(
                            TimeMapper.ToUtc(occurrence.OriginalStart), false, TimeMapper.ToUtc(occurrence.Start), TimeMapper.ToUtc(occurrence.End), s, l, b,
                            isPrivate: occurrenceSensitivity == Sensitivity.Private || occurrenceSensitivity == Sensitivity.Confidential));
                    }
                }
            }

            var item = new SourceItem(
                brief.SourceId,
                subject,
                location,
                body,
                brief.Start,
                brief.End,
                allDay,
                EwsFieldMapper.FromEws(busy),
                EwsFieldMapper.FromEws(sensitivity),
                rule,
                seriesEnd,
                exceptions,
                attachments,
                a.TryGetProperty(MapiProperties.RtfCompressed, out byte[] rtf) ? rtf : null);
            return new Mapped(item, warning);
        }

        private sealed class Mapped
        {
            public Mapped(SourceItem item, string? warning)
            {
                Item = item;
                Warning = warning;
            }

            public SourceItem Item { get; }

            public string? Warning { get; }
        }

        private sealed class Brief
        {
            public Brief(string itemId, string sourceId, bool isMaster, DateTime start, DateTime end)
            {
                ItemId = itemId;
                SourceId = sourceId;
                IsMaster = isMaster;
                Start = start;
                End = end;
            }

            public string ItemId { get; }

            public string SourceId { get; }

            public bool IsMaster { get; }

            public DateTime Start { get; }

            public DateTime End { get; }
        }
    }
}
