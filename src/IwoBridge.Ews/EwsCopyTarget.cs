using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Model;
using IwoBridge.Core.Reading;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Ews.Mapping;
using Task = System.Threading.Tasks.Task;
using TimeWindow = IwoBridge.Core.Time.TimeWindow;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Календарь-приёмник по EWS со стороны копирования.
    ///  - Копии опознаются только по метке <c>IwoBridgeSourceId</c>; события без метки этот класс не видит и не меняет.
    ///  - Копия: простое событие (без приглашений участникам, без напоминания), метка источника и отпечаток —
    ///    расширенные свойства самой копии, записываются тем же запросом, что и содержимое.
    ///  - Серия — настоящей серией: правило, затем исключения (удалённые и изменённые повторения) — их сервер находит по
    /// исходному началу. Изменение серии — пересоздание (смена правила иначе оставила бы «чужие» исключения).
    ///  - Вложения — файлами копии.
    ///  - Запись — в поясе календаря приёмника (<see cref="Transport.CalendarZones"/>), чтение — в UTC.
    /// </summary>
    public sealed class EwsCopyTarget : ICopyTarget
    {
        private const int PageSize = 500;

        // Окно поиска повторения серии вокруг его исходного начала (сервер отдаёт надмножество — сверяем точно).
        private static readonly TimeSpan OccurrenceSearch = TimeSpan.FromDays(1);

        /// <summary>Отпечаток недописанной копии: ни с одним событием не совпадает.</summary>
        private const string IncompleteHash = "incomplete";

        private readonly EwsChannel _read;
        private readonly EwsChannel _write;
        private readonly FolderId _calendar;
        private readonly EwsCalendarGateway _gateway;
        private readonly TimeZoneInfo _calendarZone;

        internal EwsCopyTarget(EwsChannel read, EwsChannel write, FolderId calendar, EwsCalendarGateway gateway, TimeZoneInfo calendarZone)
        {
            _read = read;
            _write = write;
            _calendar = calendar;
            _gateway = gateway;
            _calendarZone = calendarZone;
        }

        public async Task<IReadOnlyList<BridgeCopy>> ReadCopiesAsync(CancellationToken cancellationToken)
        {
            var found = new List<(string Id, string SourceId, bool IsMaster, DateTime Start, DateTime End, string? Hash, string? Subject)>();
            IReadOnlyList<Appointment> copies = await ListAsync(new SearchFilter.Exists(BridgeExtendedProperties.SourceId), cancellationToken).ConfigureAwait(false);
            foreach (Appointment appointment in copies)
            {
                if (appointment.TryGetProperty(BridgeExtendedProperties.SourceId, out object source) && source is string sourceId && sourceId.Length > 0
                    && appointment.TryGetProperty(AppointmentSchema.Start, out DateTime start)
                    && appointment.TryGetProperty(AppointmentSchema.End, out DateTime end))
                {
                    appointment.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type);
                    string? hash = appointment.TryGetProperty(BridgeExtendedProperties.ContentHash, out object h) ? h as string : null;
                    appointment.TryGetProperty(ItemSchema.Subject, out string subject);
                    found.Add((appointment.Id.UniqueId, sourceId, type == AppointmentType.RecurringMaster, TimeMapper.ToUtc(start), TimeMapper.ToUtc(end), hash, subject));
                }
            }

            // Охват серии — до последнего повторения: данные серии дочитывает шлюз (тот же запрос, что при очистке, DRY).
            string[] masters = found.Where(f => f.IsMaster).Select(f => f.Id).ToArray();
            IReadOnlyDictionary<string, RecurrenceInfo> series = await _gateway.LoadAsync(masters, cancellationToken).ConfigureAwait(false);

            return found.Select(f =>
            {
                DateTime spanEnd = f.End;
                if (f.IsMaster)
                {
                    spanEnd = series.TryGetValue(f.Id, out RecurrenceInfo? info) && info.LastStartUtc.HasValue
                        ? info.LastStartUtc.Value + (f.End - f.Start)
                        : DateTime.MaxValue;
                }

                return new BridgeCopy(f.Id, f.SourceId, f.IsMaster ? ItemKind.RecurringMaster : ItemKind.Single, f.Start, spanEnd, f.Hash, f.Subject);
            }).ToArray();
        }

        public async Task<IReadOnlyList<OwnEvent>> ReadOwnEventsAsync(DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
        {
            // Без метки моста: события человека. Серии — все (их первое повторение может быть задолго до периода).
            IReadOnlyList<Appointment> own = await ListAsync(
                new SearchFilter.Not(new SearchFilter.Exists(BridgeExtendedProperties.SourceId)), cancellationToken).ConfigureAwait(false);
            var result = new List<OwnEvent>();
            foreach (Appointment appointment in own)
            {
                if (!appointment.TryGetProperty(AppointmentSchema.Start, out DateTime start) || !appointment.TryGetProperty(AppointmentSchema.End, out DateTime end))
                {
                    continue;
                }

                appointment.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type);
                appointment.TryGetProperty(ItemSchema.Subject, out string subject);
                bool series = type == AppointmentType.RecurringMaster;
                DateTime startUtc = TimeMapper.ToUtc(start), endUtc = TimeMapper.ToUtc(end);
                if (series || TimeWindow.Overlaps(startUtc, endUtc, windowStartUtc, windowEndUtc))
                {
                    result.Add(new OwnEvent(subject, startUtc, endUtc, series ? ItemKind.RecurringMaster : ItemKind.Single));
                }
            }

            return result;
        }

        /// <summary>Все элементы календаря под фильтром — постранично, без раскрытия серий (одно место для копий и своих событий).</summary>
        private async Task<IReadOnlyList<Appointment>> ListAsync(SearchFilter filter, CancellationToken cancellationToken)
        {
            var all = new List<Appointment>();
            for (int offset = 0; ; offset += PageSize)
            {
                var view = new ItemView(PageSize, offset, OffsetBasePoint.Beginning)
                {
                    PropertySet = new PropertySet(
                        BasePropertySet.IdOnly,
                        ItemSchema.Subject,
                        AppointmentSchema.Start,
                        AppointmentSchema.End,
                        AppointmentSchema.AppointmentType,
                        BridgeExtendedProperties.SourceId,
                        BridgeExtendedProperties.ContentHash),
                };
                view.OrderBy.Add(ItemSchema.DateTimeCreated, SortDirection.Ascending);
                FindItemsResults<Item> page = await _read
                    .RunAsync(service => service.FindItems(_calendar, filter, view), cancellationToken)
                    .ConfigureAwait(false);
                all.AddRange(page.Items.OfType<Appointment>());
                if (!page.MoreAvailable)
                {
                    return all;
                }
            }
        }

        public async Task CreateAsync(SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            // Копия либо записана целиком (событие, вложения, исключения серии), либо её нет. Вложения и исключения EWS
            // пишет отдельными запросами после самого события, а отпечаток уже в событии: недописанная копия с верным
            // отпечатком считалась бы точной навсегда. Поэтому при сбое на любом шаге созданное удаляется — и следующий
            // запуск создаст копию заново.
            ItemId id = await _write.RunAsync(
                service =>
                {
                    var appointment = new Appointment(service);
                    Fill(appointment, item, attachments, item.ContentHash);
                    try
                    {
                        appointment.Save(_calendar, SendInvitationsMode.SendToNone);
                    }
                    catch (Exception) when (!appointment.IsNew)
                    {
                        Discard(service, appointment.Id); // событие создано, а вложение — нет
                        throw;
                    }

                    return appointment.Id;
                },
                cancellationToken,
                retry: false).ConfigureAwait(false); // создание неидемпотентно: потерянный ответ + повтор = дубль

            if (item.Recurrence != null && item.Exceptions.Count > 0)
            {
                try
                {
                    await ApplyExceptionsAsync(id, item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await _write.RunAsync(service => Discard(service, id), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }

        /// <summary>Удалить недописанную копию (свою, только что созданную) — без «Удалённых»; сбой удаления не маскирует исходный.</summary>
        private static void Discard(ExchangeService service, ItemId id)
        {
            try
            {
                service.DeleteItems(new[] { id }, DeleteMode.HardDelete, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences);
            }
#pragma warning disable CA1031 // Не удалилась — у неё не будет верного отпечатка? Будет: следующий план удалит её как дубль.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        public async Task UpdateAsync(string copyItemId, SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.Recurrence != null)
            {
                // Серия переписывается целиком: удалить и создать заново (правило, исключения, вложения).
                await DeleteAsync(copyItemId, cancellationToken).ConfigureAwait(false);
                await CreateAsync(item, attachments, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Сначала содержимое с отпечатком «не дописано», настоящий отпечаток — последним запросом: прервётся посередине
            // (вложения пишутся отдельно) — отпечаток не совпадёт ни с одним событием, и следующий запуск перезапишет копию.
            // Пустая строка не годится: EWS не записывает пустое значение свойства (проверено вживую).
            await _write.RunAsync(
                service =>
                {
                    Appointment appointment = Appointment.Bind(service, new ItemId(copyItemId), new PropertySet(BasePropertySet.FirstClassProperties));
                    appointment.Attachments.Clear();
                    Fill(appointment, item, attachments, IncompleteHash);
                    appointment.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                    Appointment stamp = Appointment.Bind(service, appointment.Id, new PropertySet(BasePropertySet.IdOnly));
                    stamp.SetExtendedProperty(BridgeExtendedProperties.ContentHash, item.ContentHash);
                    stamp.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                },
                cancellationToken).ConfigureAwait(false);
        }

        public Task DeleteAsync(string copyItemId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(copyItemId))
            {
                throw new ArgumentException("Нужен идентификатор копии.", nameof(copyItemId));
            }

            // В «Удалённые», без отмен участникам. Уже удалённая копия — не ошибка: цель достигнута.
            return _write.RunAsync(
                service =>
                {
                    try
                    {
                        Appointment.Bind(service, new ItemId(copyItemId), new PropertySet(BasePropertySet.IdOnly))
                            .Delete(DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone);
                    }
                    catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
                    {
                        // Копии уже нет — цель достигнута.
                    }
                },
                cancellationToken);
        }

        /// <summary>Поля копии из события источника (одно место для создания и перезаписи — DRY).</summary>
        private void Fill(Appointment appointment, SourceItem item, IReadOnlyList<AttachmentContent> attachments, string contentHash)
        {
            (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(item.StartUtc, item.EndUtc, item.IsAllDay, _calendarZone);
            appointment.Subject = item.Subject;
            appointment.Location = item.Location;
            if (item.RtfCompressed != null)
            {
                // Описание с оформлением и картинками — исходным RTF (MapiRtf); текст и HTML Exchange построит сам.
                appointment.SetExtendedProperty(MapiProperties.RtfCompressed, item.RtfCompressed);
                appointment.SetExtendedProperty(MapiProperties.RtfInSync, true);
            }
            else
            {
                appointment.Body = new MessageBody(BodyType.Text, item.Body);
            }

            // Пояс события — пояс календаря приёмника. У новой копии он и так берётся из канала записи, а у
            // перезаписываемой (Bind) не загружен, и Exchange 2007 без него не меняет время (проверено вживую).
            appointment.StartTimeZone = _calendarZone;
            appointment.Start = start;
            appointment.End = end;
            appointment.IsAllDayEvent = item.IsAllDay;
            appointment.LegacyFreeBusyStatus = EwsFieldMapper.ToEws(item.Busy);
            appointment.Sensitivity = EwsFieldMapper.ToEws(item.Sensitivity);
            appointment.IsReminderSet = false;
            appointment.SetExtendedProperty(BridgeExtendedProperties.SourceId, item.SourceId);
            appointment.SetExtendedProperty(BridgeExtendedProperties.ContentHash, contentHash);
            if (item.Recurrence != null)
            {
                appointment.Recurrence = EwsRecurrenceMapper.ToEws(item.Recurrence);
            }

            foreach (AttachmentContent attachment in attachments)
            {
                FileAttachment file = appointment.Attachments.AddFileAttachment(attachment.Name, attachment.Content);
                if (attachment.ContentId != null)
                {
                    file.ContentId = attachment.ContentId; // картинка внутри описания (ссылка cid:)
                }
            }
        }

        /// <summary>
        /// Исключения серии: повторение находится по исходному началу (окно календаря вокруг него, точная сверка), затем
        /// удаляется или переписывается. Повторение, которого нет (правило его не даёт), пропускается.
        /// </summary>
        private async Task ApplyExceptionsAsync(ItemId masterId, SourceItem item, CancellationToken cancellationToken)
        {
            string? uid = await _read.RunAsync(
                service =>
                {
                    Appointment master = Appointment.Bind(service, masterId, new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.ICalUid));
                    return master.TryGetProperty(AppointmentSchema.ICalUid, out string value) ? value : null;
                },
                cancellationToken).ConfigureAwait(false);

            // Без UID повторение своей серии не отличить от чужого события в то же время — а дальше удаление. Не рисковать.
            if (string.IsNullOrEmpty(uid))
            {
                throw new InvalidOperationException("Сервер не вернул идентификатор созданной серии — исключения серии не применены.");
            }

            foreach (SourceException exception in item.Exceptions)
            {
                DateTime original = exception.OriginalStartUtc;
                ItemId? occurrenceId = await _read.RunAsync(
                    service =>
                    {
                        var view = new CalendarView(original - OccurrenceSearch, original + OccurrenceSearch)
                        {
                            PropertySet = new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.Start, AppointmentSchema.ICalUid, AppointmentSchema.AppointmentType),
                        };
                        FindItemsResults<Appointment> found = service.FindAppointments(_calendar, view);
                        Appointment? match = found.Items.FirstOrDefault(a =>
                            a.TryGetProperty(AppointmentSchema.ICalUid, out string u) && string.Equals(u, uid, StringComparison.Ordinal)
                            && a.TryGetProperty(AppointmentSchema.Start, out DateTime s) && TimeMapper.ToUtc(s) == original);
                        return match?.Id;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (occurrenceId is null)
                {
                    continue;
                }

                if (exception.IsDeleted)
                {
                    await _write.RunAsync(
                        service => service.DeleteItems(new[] { occurrenceId }, DeleteMode.HardDelete, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _write.RunAsync(
                    service =>
                    {
                        Appointment occurrence = Appointment.Bind(service, occurrenceId, new PropertySet(BasePropertySet.IdOnly));
                        (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(exception.StartUtc!.Value, exception.EndUtc!.Value, item.IsAllDay, _calendarZone);
                        // Exchange 2007: при смене времени нужен пояс события (EWS Managed API проверяет до отправки).
                        occurrence.StartTimeZone = _calendarZone;
                        occurrence.Start = start;
                        occurrence.End = end;
                        occurrence.Subject = exception.Subject ?? item.Subject;
                        occurrence.Location = exception.Location ?? item.Location;
                        occurrence.Body = new MessageBody(BodyType.Text, exception.Body ?? item.Body);
                        occurrence.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
