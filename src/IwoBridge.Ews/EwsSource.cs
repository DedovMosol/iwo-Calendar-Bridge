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
    /// исключения, имена вложений). Содержимое вложений — по запросу, только для того, что копируется.
    /// Элемент, который не прочитался, делает чтение неполным: по неполному чтению копии не удаляются.
    /// </summary>
    public sealed class EwsSource : ISourceCalendar
    {
        private const int PageSize = 500;
        private const int BindBatch = 50;

        private readonly EwsChannel _channel;
        private readonly FolderId _calendar;
        private readonly EwsCalendarGateway _gateway;
        private readonly object _sync = new object();
        private Dictionary<string, string> _itemIdBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        internal EwsSource(string label, EwsChannel channel, FolderId calendar, EwsCalendarGateway gateway)
        {
            Label = label;
            _channel = channel;
            _calendar = calendar;
            _gateway = gateway;
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
            List<Brief> all = await ListAsync(cancellationToken).ConfigureAwait(false);
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
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            int unreadable = 0;
            foreach (IReadOnlyList<Brief> batch in Batching.Chunk(chosen, BindBatch))
            {
                IReadOnlyList<SourceItem?> read = await ReadFullAsync(batch, series, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < batch.Count; i++)
                {
                    if (read[i] is SourceItem item)
                    {
                        items.Add(item);
                        ids[item.SourceId] = batch[i].ItemId;
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

            return new SourceRead(items, unreadable == 0 ? null : "Не прочитано событий источника: " + unreadable + ".");
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
                    var list = new List<AttachmentContent>();
                    foreach (FileAttachment file in source.Attachments.OfType<FileAttachment>())
                    {
                        file.Load();
                        list.Add(new AttachmentContent(file.Name, file.Content));
                    }

                    return list;
                },
                cancellationToken);
        }

        private async Task<List<Brief>> ListAsync(CancellationToken cancellationToken)
        {
            var all = new List<Brief>();
            for (int offset = 0; ; offset += PageSize)
            {
                var view = new ItemView(PageSize, offset, OffsetBasePoint.Beginning)
                {
                    PropertySet = new PropertySet(
                        BasePropertySet.IdOnly, AppointmentSchema.Start, AppointmentSchema.End, AppointmentSchema.AppointmentType, AppointmentSchema.ICalUid),
                };
                view.OrderBy.Add(ItemSchema.DateTimeCreated, SortDirection.Ascending);
                FindItemsResults<Item> page = await _channel
                    .RunAsync(service => service.FindItems(_calendar, view), cancellationToken)
                    .ConfigureAwait(false);
                foreach (Appointment a in page.Items.OfType<Appointment>())
                {
                    if (a.TryGetProperty(AppointmentSchema.Start, out DateTime start) && a.TryGetProperty(AppointmentSchema.End, out DateTime end))
                    {
                        a.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type);
                        string sourceId = a.TryGetProperty(AppointmentSchema.ICalUid, out string uid) && !string.IsNullOrEmpty(uid) ? uid : a.Id.UniqueId;
                        all.Add(new Brief(a.Id.UniqueId, sourceId, type == AppointmentType.RecurringMaster, TimeMapper.ToUtc(start), TimeMapper.ToUtc(end)));
                    }
                }

                if (!page.MoreAvailable)
                {
                    return all;
                }
            }
        }

        private Task<IReadOnlyList<SourceItem?>> ReadFullAsync(
            IReadOnlyList<Brief> batch, IReadOnlyDictionary<string, RecurrenceInfo> series, CancellationToken cancellationToken)
        {
            var properties = new PropertySet(
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
                AppointmentSchema.DeletedOccurrences)
            {
                RequestedBodyType = BodyType.Text,
            };

            return _channel.RunAsync<IReadOnlyList<SourceItem?>>(
                service =>
                {
                    ServiceResponseCollection<GetItemResponse> responses = service.BindToItems(batch.Select(b => new ItemId(b.ItemId)), properties);
                    var result = new SourceItem?[batch.Count];
                    for (int i = 0; i < batch.Count && i < responses.Count; i++)
                    {
                        if (responses[i].Result == ServiceResult.Success && responses[i].Item is Appointment appointment)
                        {
                            try
                            {
                                result[i] = Map(service, batch[i], appointment, series);
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

        private static SourceItem Map(ExchangeService service, Brief brief, Appointment a, IReadOnlyDictionary<string, RecurrenceInfo> series)
        {
            a.TryGetProperty(ItemSchema.Subject, out string subject);
            a.TryGetProperty(AppointmentSchema.Location, out string location);
            string body = a.TryGetProperty(ItemSchema.Body, out MessageBody messageBody) ? messageBody?.Text ?? string.Empty : string.Empty;
            a.TryGetProperty(AppointmentSchema.IsAllDayEvent, out bool allDay);
            a.TryGetProperty(AppointmentSchema.LegacyFreeBusyStatus, out LegacyFreeBusyStatus busy);
            a.TryGetProperty(ItemSchema.Sensitivity, out Sensitivity sensitivity);
            var attachments = new List<SyncAttachment>();
            if (a.TryGetProperty(ItemSchema.Attachments, out AttachmentCollection files))
            {
                // Размер вложения EWS отдаёт начиная с Exchange 2010; для отпечатка хватает имени.
                attachments.AddRange(files.OfType<FileAttachment>().Select(f => new SyncAttachment(f.Name, 0)));
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
                        var props = new PropertySet(BasePropertySet.IdOnly, ItemSchema.Subject, ItemSchema.Body, AppointmentSchema.Location)
                        {
                            RequestedBodyType = BodyType.Text,
                        };
                        Appointment changed = Appointment.Bind(service, occurrence.ItemId, props);
                        changed.TryGetProperty(ItemSchema.Subject, out string s);
                        changed.TryGetProperty(AppointmentSchema.Location, out string l);
                        string b = changed.TryGetProperty(ItemSchema.Body, out MessageBody mb) ? mb?.Text ?? string.Empty : string.Empty;
                        exceptions.Add(new SourceException(
                            TimeMapper.ToUtc(occurrence.OriginalStart), false, TimeMapper.ToUtc(occurrence.Start), TimeMapper.ToUtc(occurrence.End), s, l, b));
                    }
                }
            }

            return new SourceItem(
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
                attachments);
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
