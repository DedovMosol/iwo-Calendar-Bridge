using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Collections;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using IwoBridge.Core.Reading;
using IwoBridge.Core.Time;
using IwoBridge.Ews.Mapping;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Сетевые границы приёмника поверх EWS Managed API: постраничное перечисление основного календаря,
    /// дочитывание данных серий, раскрытие окна и пакетное удаление. Оркестрацию (цикл страниц, полнота,
    /// план, пакеты) выполняют классы ядра.
    ///
    /// Потокобезопасность: безопасность экземпляра <see cref="ExchangeService"/> при параллельном
    /// использовании не гарантируется, поэтому все вызовы EWS сериализуются семафором — в полёте всегда не
    /// больше одного запроса (заодно соблюдается привязка NTLM к соединению). Отмена кооперативная: ожидание
    /// очереди прерывается сразу, а начатый запрос (он не отменяем) доводится до конца и только потом
    /// освобождает очередь — экземпляр сервиса никогда не используется двумя потоками одновременно.
    /// </summary>
    public sealed class EwsCalendarGateway
        : ITargetPageReader, ITargetDeleter, ITargetRecurrenceReader, ITargetOccurrenceReader
    {
        private const int GetItemBatchSize = 50;
        private const int MaxOccurrencesPerView = 1000;

        // Предел диапазона CalendarView по документации EWS (ResponseCode, ErrorCalendarViewRangeTooBig) — 2 года;
        // режем по году с запасом.
        private static readonly TimeSpan MaxCalendarViewSpan = TimeSpan.FromDays(366);

        // Неустранимое усечение в окне не длиннее суток означает, что раскрыть его полностью нельзя.
        private static readonly TimeSpan MinSplitSpan = TimeSpan.FromDays(1);

        private static readonly DateTime NonAppointmentPlaceholder =
            new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Класс для события без обязательных полей: не начинается с IPM.Appointment, поэтому не удаляется.</summary>
        private const string CorruptAppointmentClass = "IPM.Unreadable";

        private readonly IReadOnlyList<ExchangeService> _services;
        private readonly FolderId _calendarFolderId;
        private readonly RetryPolicy _retry;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private int _active;

        /// <param name="service">Настроенный сервис (<see cref="Transport.ExchangeServiceFactory"/>).</param>
        /// <param name="mailboxAddress">Целевой ящик; пусто — ящик учётной записи.</param>
        /// <param name="retry">
        /// Повторы временных ошибок (таймаут, «сервер занят»); <c>null</c> — без повторов. Повторять безопасно: чтения
        /// ничего не меняют, удаление идемпотентно.
        /// </param>
        public EwsCalendarGateway(ExchangeService service, string? mailboxAddress, RetryPolicy? retry = null)
            : this(new[] { service ?? throw new ArgumentNullException(nameof(service)) }, mailboxAddress, retry)
        {
        }

        /// <summary>
        /// Шлюз с несколькими адресами одного сервера (основной, резервный). Запрос идёт по текущему адресу; если до
        /// него не достучаться (<see cref="ErrorClassifier.IsUnreachable"/>: обрыв, отказ соединения, нет ответа),
        /// тот же запрос сразу повторяется по следующему адресу, и дальше шлюз работает через него — без возврата на
        /// каждом запросе. Ошибки входа, прав и сертификата адрес не меняют: по другому адресу ответил бы тот же сервер.
        /// Переход безопасен и посреди работы: за адресами — один и тот же ящик, чтения ничего не меняют, а удаление
        /// идемпотентно (удалённое до обрыва при повторе вернётся как «уже отсутствует»).
        /// Переход на другой адрес — до повторов <paramref name="retry"/>: недоступный адрес не держит работу на время
        /// всех попыток с паузами.
        /// </summary>
        /// <param name="services">Сервисы в порядке попытки; у каждого своя группа соединений и проверка сертификата.</param>
        public EwsCalendarGateway(IReadOnlyList<ExchangeService> services, string? mailboxAddress, RetryPolicy? retry = null)
        {
            if (services is null || services.Count == 0 || services.Any(s => s is null))
            {
                throw new ArgumentException("Нужен хотя бы один сервис EWS.", nameof(services));
            }

            _services = services.ToArray();
            _calendarFolderId = string.IsNullOrWhiteSpace(mailboxAddress)
                ? new FolderId(WellKnownFolderName.Calendar)
                : new FolderId(WellKnownFolderName.Calendar, new Mailbox(mailboxAddress));
            _retry = retry ?? RetryPolicy.None;
        }

        /// <summary>Номер адреса, через который сейчас идут запросы (0 — основной).</summary>
        public int ActiveEndpointIndex => Volatile.Read(ref _active);

        /// <summary>
        /// Проверка подключения: привязка к основному календарю целевого ящика и число элементов в нём. Один лёгкий
        /// запрос <c>GetFolder</c> проходит весь путь — TLS и сертификат, NTLM, права на ящик, наличие календаря.
        /// Без повторов: человек ждёт ответа, и «нет связи» должно появиться сразу.
        /// </summary>
        public async Task<int> CountItemsAsync(CancellationToken cancellationToken)
        {
            var propertySet = new PropertySet(BasePropertySet.IdOnly, FolderSchema.TotalCount);
            Folder folder = await RunExclusiveAsync(
                service => Folder.Bind(service, _calendarFolderId, propertySet), cancellationToken, retry: false).ConfigureAwait(false);
            return folder.TotalCount;
        }

        public async Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (pageSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            var view = new ItemView(pageSize, offset, OffsetBasePoint.Beginning)
            {
                Traversal = ItemTraversal.Shallow,
                PropertySet = new PropertySet(
                    BasePropertySet.IdOnly,
                    ItemSchema.ItemClass,
                    ItemSchema.Subject,
                    AppointmentSchema.Start,
                    AppointmentSchema.End,
                    AppointmentSchema.AppointmentType,
                    BridgeExtendedProperties.SourceId),
            };

            // Стабильный порядок для постраничного обхода: новые элементы добавляются в конец и не сдвигают
            // прочитанные страницы; сдвиги от удалений обнаруживает якорь в CalendarSnapshotReader. Дата создания не
            // уникальна (импорт, перенос ящика создают тысячи элементов одной секундой), и порядок внутри равных мог бы
            // меняться от запроса к запросу — якорь принял бы это за сдвиг. Вторичные ключи сужают такие ничьи.
            view.OrderBy.Add(ItemSchema.DateTimeCreated, SortDirection.Ascending);
            view.OrderBy.Add(AppointmentSchema.Start, SortDirection.Ascending);
            view.OrderBy.Add(ItemSchema.Subject, SortDirection.Ascending);

            FindItemsResults<Item> results = await RunExclusiveAsync(
                service => service.FindItems(_calendarFolderId, view), cancellationToken).ConfigureAwait(false);

            var items = new List<TargetItemInfo>(results.Items.Count);
            foreach (Item item in results.Items)
            {
                items.Add(MapItem(item));
            }

            return new TargetPage(items, results.TotalCount, !results.MoreAvailable);
        }

        public async Task<IReadOnlyList<DeleteResult>> DeleteAsync(
            IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken)
        {
            if (itemIds is null)
            {
                throw new ArgumentNullException(nameof(itemIds));
            }

            if (itemIds.Count == 0)
            {
                return Array.Empty<DeleteResult>();
            }

            List<ItemId> ids = itemIds.Select(id => new ItemId(id)).ToList();
            DeleteMode deleteMode =
                mode == TargetDeleteMode.HardDelete ? DeleteMode.HardDelete : DeleteMode.MoveToDeletedItems;

            // SendToNone — никаких отмен и отказов участникам: копии убираются молча. AffectedTaskOccurrences по
            // документации DeleteItem обязателен, если среди элементов окажется задача; для событий он игнорируется.
            ServiceResponseCollection<ServiceResponse> responses = await RunExclusiveAsync(
                service => service.DeleteItems(ids, deleteMode, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
                cancellationToken).ConfigureAwait(false);

            var results = new List<DeleteResult>(responses.Count);
            foreach (ServiceResponse response in responses)
            {
                results.Add(EwsDeleteResultMapper.Map(response.Result, response.ErrorCode));
            }

            return results;
        }

        public async Task<IReadOnlyDictionary<string, RecurrenceInfo>> LoadAsync(
            IReadOnlyList<string> masterItemIds, CancellationToken cancellationToken)
        {
            if (masterItemIds is null)
            {
                throw new ArgumentNullException(nameof(masterItemIds));
            }

            var result = new Dictionary<string, RecurrenceInfo>(StringComparer.Ordinal);
            if (masterItemIds.Count == 0)
            {
                return result;
            }

            var propertySet = new PropertySet(
                BasePropertySet.IdOnly,
                AppointmentSchema.FirstOccurrence,
                AppointmentSchema.LastOccurrence,
                AppointmentSchema.ModifiedOccurrences,
                AppointmentSchema.Recurrence,
                AppointmentSchema.ICalUid);

            foreach (IReadOnlyList<string> batch in Batching.Chunk(masterItemIds, GetItemBatchSize))
            {
                List<ItemId> ids = batch.Select(id => new ItemId(id)).ToList();
                ServiceResponseCollection<GetItemResponse> responses = await RunExclusiveAsync(
                    service => service.BindToItems(ids, propertySet), cancellationToken).ConfigureAwait(false);

                // Ответы EWS идут в порядке запроса.
                int count = Math.Min(responses.Count, batch.Count);
                for (int i = 0; i < count; i++)
                {
                    GetItemResponse response = responses[i];
                    if (response.Result == ServiceResult.Success && response.Item is Appointment appointment)
                    {
                        RecurrenceInfo? info = ReadRecurrence(appointment);
                        if (info != null)
                        {
                            result[batch[i]] = info;
                        }
                    }
                }
            }

            return result;
        }

        public async Task<OccurrenceReadResult> ReadOccurrencesAsync(
            DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
        {
            DateTime start = TimeMapper.ToUtc(windowStartUtc);
            DateTime end = TimeMapper.ToUtc(windowEndUtc);
            var collected = new List<TargetOccurrence>();

            DateTime segmentStart = start;
            while (segmentStart < end)
            {
                DateTime segmentEnd = end - segmentStart > MaxCalendarViewSpan ? segmentStart + MaxCalendarViewSpan : end;
                bool complete = await ReadSegmentAsync(segmentStart, segmentEnd, collected, cancellationToken)
                    .ConfigureAwait(false);
                if (!complete)
                {
                    return new OccurrenceReadResult(collected, isComplete: false);
                }

                segmentStart = segmentEnd;
            }

            return new OccurrenceReadResult(collected, isComplete: true);
        }

        private async Task<bool> ReadSegmentAsync(
            DateTime startUtc, DateTime endUtc, List<TargetOccurrence> accumulator, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endUtc <= startUtc)
            {
                return true;
            }

            var view = new CalendarView(startUtc, endUtc, MaxOccurrencesPerView)
            {
                PropertySet = new PropertySet(
                    BasePropertySet.IdOnly,
                    ItemSchema.Subject,
                    AppointmentSchema.Start,
                    AppointmentSchema.End,
                    AppointmentSchema.AppointmentType,
                    AppointmentSchema.ICalUid),
            };

            FindItemsResults<Appointment>? results;
            try
            {
                results = await RunExclusiveAsync(
                    service => service.FindAppointments(_calendarFolderId, view), cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorExceededFindCountLimit)
            {
                // Лимит выборки (EWSFindCountLimit, Exchange 2010+) администратор мог понизить: сервер не усекает
                // ответ, а отказывает. Ответ тот же, что на усечение, — дробить окно.
                results = null;
            }

            if (results != null && !results.MoreAvailable)
            {
                foreach (Appointment appointment in results.Items)
                {
                    // Битый элемент без начала, конца или вида не роняет чтение: он просто не попадает в обрезку —
                    // то есть не удаляется (отказ в безопасную сторону).
                    if (appointment.TryGetProperty(AppointmentSchema.Start, out DateTime start)
                        && appointment.TryGetProperty(AppointmentSchema.End, out DateTime end)
                        && appointment.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type))
                    {
                        appointment.TryGetProperty(AppointmentSchema.ICalUid, out string uid);
                        appointment.TryGetProperty(ItemSchema.Subject, out string subject);
                        accumulator.Add(EwsItemMapper.ToOccurrence(
                            appointment.Id.UniqueId, uid, TimeMapper.ToUtc(start), TimeMapper.ToUtc(end), type, subject));
                    }
                }

                return true;
            }

            if (endUtc - startUtc <= MinSplitSpan)
            {
                return false;
            }

            DateTime middle = startUtc.AddTicks((endUtc - startUtc).Ticks / 2);
            bool left = await ReadSegmentAsync(startUtc, middle, accumulator, cancellationToken).ConfigureAwait(false);
            if (!left)
            {
                return false;
            }

            return await ReadSegmentAsync(middle, endUtc, accumulator, cancellationToken).ConfigureAwait(false);
        }

        private static RecurrenceInfo? ReadRecurrence(Appointment appointment)
        {
            // Без первого повторения границы серии неизвестны — серия считается непрочитанной, и предпросмотр
            // запретит удаление: отказ лучше неверной классификации.
            if (!appointment.TryGetProperty(AppointmentSchema.FirstOccurrence, out OccurrenceInfo first) || first == null)
            {
                return null;
            }

            appointment.TryGetProperty(AppointmentSchema.LastOccurrence, out OccurrenceInfo last);
            appointment.TryGetProperty(AppointmentSchema.Recurrence, out Recurrence recurrence);
            appointment.TryGetProperty(AppointmentSchema.ModifiedOccurrences, out OccurrenceInfoCollection modified);
            appointment.TryGetProperty(AppointmentSchema.ICalUid, out string uid);

            // Бессрочная серия не имеет последнего повторения, даже если сервер вернул вычисленное значение.
            bool neverEnds = recurrence != null && !recurrence.HasEnd;
            DateTime? lastStart = neverEnds || last == null ? (DateTime?)null : TimeMapper.ToUtc(last.Start);

            var modifiedSpans = new List<OccurrenceSpan>();
            if (modified != null)
            {
                foreach (OccurrenceInfo occurrence in modified)
                {
                    modifiedSpans.Add(new OccurrenceSpan(TimeMapper.ToUtc(occurrence.Start), TimeMapper.ToUtc(occurrence.End)));
                }
            }

            return new RecurrenceInfo(TimeMapper.ToUtc(first.Start), lastStart, uid, modifiedSpans);
        }

        private static TargetItemInfo MapItem(Item item)
        {
            string id = item.Id.UniqueId;
            string? changeKey = item.Id.ChangeKey;
            item.TryGetProperty(ItemSchema.ItemClass, out string itemClass);
            item.TryGetProperty(ItemSchema.Subject, out string subject);
            string? bridgeSourceId =
                item.TryGetProperty(BridgeExtendedProperties.SourceId, out object value) && value is string text && text.Length > 0
                    ? text
                    : null;

            if (item is Appointment appointment
                && appointment.TryGetProperty(AppointmentSchema.Start, out DateTime start)
                && appointment.TryGetProperty(AppointmentSchema.End, out DateTime end)
                && appointment.TryGetProperty(AppointmentSchema.AppointmentType, out AppointmentType type))
            {
                bool isMaster = type == AppointmentType.RecurringMaster;
                string appointmentClass = string.IsNullOrEmpty(itemClass) ? "IPM.Appointment" : itemClass;
                return EwsItemMapper.ToTargetItem(
                    id,
                    changeKey,
                    appointmentClass,
                    isMaster,
                    TimeMapper.ToUtc(start),
                    TimeMapper.ToUtc(end),
                    null,
                    null,
                    bridgeSourceId,
                    subject);
            }

            // Не событие (или битое событие без начала, конца, вида): в снимке остаётся, чтобы полнота сходилась с числом
            // на сервере, но классом «не событие» — такое не удаляется и видно в списке «Останется».
            string otherClass = item is Appointment ? CorruptAppointmentClass : string.IsNullOrEmpty(itemClass) ? "IPM.Unknown" : itemClass;
            return EwsItemMapper.ToTargetItem(
                id,
                changeKey,
                otherClass,
                false,
                NonAppointmentPlaceholder,
                NonAppointmentPlaceholder,
                null,
                null,
                bridgeSourceId,
                subject);
        }

        /// <summary>
        /// Один вызов EWS под семафором (в полёте не больше одного запроса), с переходом на другой адрес при
        /// недоступности и с повторами временных ошибок. Пауза между попытками — вне семафора: ожидание не держит
        /// очередь.
        /// </summary>
        private Task<T> RunExclusiveAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken, bool retry = true)
        {
            return retry
                ? _retry.ExecuteAsync(() => RunOnceAsync(work, cancellationToken), cancellationToken)
                : RunOnceAsync(work, cancellationToken);
        }

        /// <summary>
        /// Попытка по текущему адресу; недоступен — сразу по следующему (каждый адрес не больше одного раза за
        /// попытку). Удачный адрес становится текущим. Номер адреса меняется только под семафором — гонок нет.
        /// </summary>
        private async Task<T> RunOnceAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int index = _active;
                for (int tried = 1; ; tried++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExchangeService service = _services[index];
                    try
                    {
                        T result = await System.Threading.Tasks.Task.Run(() => work(service)).ConfigureAwait(false);
                        Volatile.Write(ref _active, index);
                        return result;
                    }
                    catch (Exception ex) when (tried < _services.Count
                        && !cancellationToken.IsCancellationRequested
                        && ErrorClassifier.IsUnreachable(EwsErrorMapper.MapException(ex)))
                    {
                        index = (index + 1) % _services.Count;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
