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
        // Окно поиска повторения серии вокруг его ожидаемого начала (сервер отдаёт все повторения окна — выбираем ближайшее).
        private static readonly TimeSpan OccurrenceSearch = TimeSpan.FromDays(1);

        // Насколько начало повторения в копии может отличаться от ожидаемого: повторения одной серии отстоят друг от
        // друга не меньше чем на сутки, поэтому ближайшее в пределах половины суток — именно то. Расхождение бывает, когда
        // пояс источника переходит на летнее время, а пояс календаря приёмника — постоянное смещение.
        private static readonly TimeSpan OccurrenceTolerance = TimeSpan.FromHours(12);

        private static readonly PropertySet ListProperties = new PropertySet(
            BasePropertySet.IdOnly,
            ItemSchema.Subject,
            AppointmentSchema.Start,
            AppointmentSchema.End,
            AppointmentSchema.AppointmentType,
            BridgeExtendedProperties.SourceId,
            BridgeExtendedProperties.ContentHash);

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

        /// <summary>
        /// Все элементы календаря под фильтром, без раскрытия серий (одно место для копий и своих событий). Неполный
        /// перечень — отказ: пропущенная копия выглядела бы отсутствующей, и появился бы дубль.
        /// </summary>
        private async Task<IReadOnlyList<Appointment>> ListAsync(SearchFilter filter, CancellationToken cancellationToken)
        {
            (IReadOnlyList<Item> found, bool complete) = await EwsPaging
                .FindAllAsync(_read, _calendar, filter, ListProperties, cancellationToken)
                .ConfigureAwait(false);
            if (!complete)
            {
                throw new InvalidOperationException(
                    "Календарь приёмника менялся во время чтения, и перечень событий не сошёлся. Повторите копирование чуть позже.");
            }

            return found.OfType<Appointment>().ToArray();
        }

        /// <summary>
        /// Копия либо записана целиком, либо её нет. Шаги — отдельными запросами: событие с отпечатком «не дописано»,
        /// вложения по одному (крупные вложения не складываются в один запрос сверх предела сервера), исключения серии,
        /// настоящий отпечаток последним. Сбой на любом шаге после создания — созданное удаляется. Не удалилось (связь
        /// пропала) — у копии остаётся отпечаток «не дописано», и следующий запуск её перезапишет.
        /// </summary>
        public async Task<IReadOnlyList<string>> CreateAsync(SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            ItemId id = await _write.RunOnceNonIdempotentAsync(
                service =>
                {
                    var appointment = new Appointment(service);
                    Fill(appointment, item, IncompleteHash);
                    appointment.Save(_calendar, SendInvitationsMode.SendToNone);
                    return appointment.Id;
                },
                cancellationToken).ConfigureAwait(false);

            try
            {
                await AddAttachmentsAsync(id, attachments, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> remarks = item.Recurrence != null && item.Exceptions.Count > 0
                    ? await ApplyExceptionsAsync(id, item, cancellationToken).ConfigureAwait(false)
                    : Array.Empty<string>();
                await StampAsync(id, item.ContentHash, cancellationToken).ConfigureAwait(false);
                return remarks;
            }
            catch (Exception)
            {
                await _write.RunAsync(service => Discard(service, id), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Удалить недописанную копию (свою, только что созданную) — без «Удалённых»; сбой удаления не маскирует исходный.</summary>
        private static void Discard(ExchangeService service, ItemId id)
        {
            try
            {
                service.DeleteItems(new[] { id }, DeleteMode.HardDelete, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences);
            }
#pragma warning disable CA1031 // Не удалилась — у неё отпечаток «не дописано»: следующий запуск её перезапишет.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        public async Task<IReadOnlyList<string>> UpdateAsync(
            string copyItemId, SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (item.Recurrence != null)
            {
                // Серия переписывается целиком: новая копия (правило, исключения, вложения), затем удаление прежней. В таком
                // порядке сбой создания оставляет прежнюю копию, а сбой удаления — две копии, лишнюю уберёт следующий запуск.
                IReadOnlyList<string> remarks = await CreateAsync(item, attachments, cancellationToken).ConfigureAwait(false);
                await DeleteAsync(copyItemId, cancellationToken).ConfigureAwait(false);
                return remarks;
            }

            // Сначала содержимое с отпечатком «не дописано», настоящий отпечаток — последним запросом: прервётся посередине
            // (вложения пишутся отдельно) — отпечаток не совпадёт ни с одним событием, и следующий запуск перезапишет копию.
            // Пустая строка не годится: EWS не записывает пустое значение свойства (проверено вживую).
            var id = new ItemId(copyItemId);
            await _write.RunAsync(
                service =>
                {
                    Appointment appointment = Appointment.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, ItemSchema.Attachments));
                    appointment.Attachments.Clear();
                    Fill(appointment, item, IncompleteHash);
                    appointment.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                },
                cancellationToken).ConfigureAwait(false);
            await AddAttachmentsAsync(id, attachments, cancellationToken).ConfigureAwait(false);
            await StampAsync(id, item.ContentHash, cancellationToken).ConfigureAwait(false);
            return Array.Empty<string>();
        }

        public Task DeleteAsync(string copyItemId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(copyItemId))
            {
                throw new ArgumentException("Нужен идентификатор копии.", nameof(copyItemId));
            }

            // В «Удалённые», без отмен участникам, одним запросом. Уже удалённая копия — не ошибка: цель достигнута.
            return _write.RunAsync(
                service => RequireDeleted(service.DeleteItems(
                    new[] { new ItemId(copyItemId) }, DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences)),
                cancellationToken);
        }

        /// <summary>
        /// DeleteItems сообщает ошибки в ответе, а не исключением. Элемента уже нет — цель достигнута; другая ошибка —
        /// отказ (иначе удаление считалось бы сделанным). Код сервера сохраняется в исключении: временные ошибки
        /// (ящик переносится, сервер занят) повторяются, отказ в доступе останавливает пакет, как у остальных запросов.
        /// </summary>
        private static void RequireDeleted(ServiceResponseCollection<ServiceResponse> responses)
        {
            foreach (ServiceResponse response in responses)
            {
                if (response.Result == ServiceResult.Error
                    && response.ErrorCode != ServiceError.ErrorItemNotFound
                    && response.ErrorCode != ServiceError.ErrorCalendarOccurrenceIsDeletedFromRecurrence)
                {
                    throw new EwsItemErrorException(response.ErrorCode, response.ErrorMessage);
                }
            }
        }

        /// <summary>Вложения — по одному на запрос: размер запроса ограничен сервером (у Exchange 2007 — несколько МБ).</summary>
        private async Task AddAttachmentsAsync(ItemId id, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            foreach (AttachmentContent attachment in attachments)
            {
                await _write.RunOnceNonIdempotentAsync(
                    service =>
                    {
                        Appointment appointment = Appointment.Bind(service, id, new PropertySet(BasePropertySet.IdOnly));
                        FileAttachment file = appointment.Attachments.AddFileAttachment(attachment.Name, attachment.Content);
                        if (attachment.ContentId != null)
                        {
                            file.ContentId = attachment.ContentId; // картинка внутри описания (ссылка cid:)
                        }

                        appointment.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Настоящий отпечаток — последним шагом записи: до него копия считается недописанной.</summary>
        private Task StampAsync(ItemId id, string contentHash, CancellationToken cancellationToken)
        {
            return _write.RunAsync(
                service =>
                {
                    Appointment stamp = Appointment.Bind(service, id, new PropertySet(BasePropertySet.IdOnly));
                    stamp.SetExtendedProperty(BridgeExtendedProperties.ContentHash, contentHash);
                    stamp.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                },
                cancellationToken);
        }

        /// <summary>Поля копии из события источника (одно место для создания и перезаписи — DRY).</summary>
        private void Fill(Appointment appointment, SourceItem item, string contentHash)
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
        }

        /// <summary>
        /// Исключения серии: повторение копии находится по исходному началу (у события «на весь день» — по местной дате
        /// календаря приёмника), затем удаляется или переписывается.
        ///  - Удалённого повторения в копии нет — цель достигнута.
        ///  - Изменённого повторения в копии нет (правило серии в приёмнике не даёт такой даты) — замечание: копия
        ///    записывается без этого изменения, человек видит его в итоге один раз. Отказ здесь не помог бы: та же серия
        ///    пересоздавалась бы и удалялась при каждом запуске, а копия оставалась бы прежней.
        ///  - Ошибка удаления повторения (DeleteItems сообщает её в ответе) — отказ.
        /// </summary>
        private async Task<IReadOnlyList<string>> ApplyExceptionsAsync(ItemId masterId, SourceItem item, CancellationToken cancellationToken)
        {
            var remarks = new List<string>();
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
                DateTime expected = ExpectedOccurrenceStart(exception.OriginalStartUtc, item.IsAllDay);
                ItemId? occurrenceId = await _read.RunAsync(
                    service =>
                    {
                        var view = new CalendarView(expected - OccurrenceSearch, expected + OccurrenceSearch)
                        {
                            PropertySet = new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.Start, AppointmentSchema.ICalUid),
                        };
                        FindItemsResults<Appointment> found = service.FindAppointments(_calendar, view);
                        return found.Items
                            .Where(a => a.TryGetProperty(AppointmentSchema.ICalUid, out string u) && string.Equals(u, uid, StringComparison.Ordinal))
                            .Select(a => (a.Id, Distance: a.TryGetProperty(AppointmentSchema.Start, out DateTime s) ? (TimeMapper.ToUtc(s) - expected).Duration() : TimeSpan.MaxValue))
                            .Where(a => a.Distance < OccurrenceTolerance)
                            .OrderBy(a => a.Distance)
                            .Select(a => a.Id)
                            .FirstOrDefault();
                    },
                    cancellationToken).ConfigureAwait(false);

                if (occurrenceId is null)
                {
                    if (!exception.IsDeleted)
                    {
                        remarks.Add(
                            "изменение повторения от "
                            + TimeZoneInfo.ConvertTimeFromUtc(expected, _calendarZone).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                            + " не перенесено: в копии серии нет такого повторения");
                    }

                    continue;
                }

                if (exception.IsDeleted)
                {
                    await _write.RunAsync(
                        service => RequireDeleted(service.DeleteItems(
                            new[] { occurrenceId }, DeleteMode.HardDelete, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences)),
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
                        if (exception.IsPrivate)
                        {
                            occurrence.Sensitivity = Sensitivity.Private;
                        }

                        occurrence.Update(ConflictResolutionMode.AlwaysOverwrite, SendInvitationsOrCancellationsMode.SendToNone);
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return remarks;
        }

        /// <summary>
        /// Где копия серии ставит повторение с этим исходным началом: обычное событие — в тот же момент, «на весь день» —
        /// в полночь той же местной даты календаря приёмника (<see cref="EwsFieldMapper.TimesForWrite"/>, DRY).
        /// </summary>
        private DateTime ExpectedOccurrenceStart(DateTime originalStartUtc, bool isAllDay)
        {
            if (!isAllDay)
            {
                return originalStartUtc;
            }

            DateTime local = EwsFieldMapper.TimesForWrite(originalStartUtc, originalStartUtc, true, _calendarZone).Start;
            return TimeZoneInfo.ConvertTimeToUtc(local, _calendarZone);
        }
    }
}
