using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;

namespace IwoBridge.Outlook
{
    /// <summary>
    /// Календарь Outlook как источник копирования — только чтение, ничего в Outlook не меняется.
    ///
    /// Чтение в два шага:
    ///  1. Таблица папки (<c>Folder.GetTable</c>) — быстрый перечень всех событий без открытия каждого: идентификатор,
    ///     начало, конец, признак серии и конец серии, метка копии моста. По ней отбираются события, пересекающие период
    ///     (с запасом в сутки), и запрошенные по идентификатору. Копии, сделанные этой программой (если источник сам
    ///     чей-то приёмник), не читаются: иначе при встречной настройке копировались бы копии копий.
    ///  2. Отобранные события открываются целиком: поля, правило серии, исключения, имена вложений, описание в RTF;
    ///     точная проверка попадания в период — по полям самого события.
    /// Идентификатор события — GlobalAppointmentID (не меняется при переносе в другую папку и совпадает с UID, который
    /// видит Exchange); если его нет — EntryID. Встреча копируется простым событием: участники не переносятся.
    /// Каждый объект COM освобождается сразу после использования, и на пути ошибки тоже (<see cref="Com.Release"/>).
    /// </summary>
    internal sealed class OutlookSource : ISourceCalendar
    {
        // Свойства MAPI по пространству имён: в таблице их даты приходят в UTC (а по обычным именам — в местном времени).
        private const string StartProp = "urn:schemas:calendar:dtstart";
        private const string EndProp = "urn:schemas:calendar:dtend";
        private const string RecurringProp = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/8223000B";
        private const string ClipEndProp = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/82360040";
        private const string GlobalIdProp = "http://schemas.microsoft.com/mapi/id/{6ED8DA90-450B-101B-98DA-00AA003F1305}/00030102";

        // Метка копии моста (то же именованное свойство, что пишет приёмник EWS: набор и имя — константы продукта).
        // Тип (0x001F — строка) указывается явно: именованное свойство из своего набора Outlook иначе не принимает.
        private const string BridgeMarkProp = "http://schemas.microsoft.com/mapi/string/{8E2B2E7A-0D2A-4E6E-9E8C-9C2B1A7F3D10}/IwoBridgeSourceId/0x0000001F";

        // OlAttachmentType: файл, вложенное письмо или элемент (сохраняется как .msg), ссылка, объект OLE.
        private const int OlByValue = 1;
        private const int OlEmbeddedItem = 5;

        // Запас при отборе по таблице: точное сравнение — по полям события.
        private static readonly TimeSpan Slack = TimeSpan.FromDays(1);
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

        private const int OlPrivate = 2;
        private const int OlConfidential = 3;

        private readonly OutlookSession _session;
        private readonly OutlookCalendarRef _calendar;
        private readonly object _sync = new object();
        private Dictionary<string, string> _entryBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        // Хранилище папки, открытой последним чтением: общий календарь, открытый заново по имени владельца, может лежать в
        // хранилище с другим идентификатором, чем запомненный.
        private string _storeId;

        // Номер последнего начатого чтения: итог более старого (например, вернувшегося после зависания) не затирает новый.
        private int _generation;

        public OutlookSource(OutlookSession session, OutlookCalendarRef calendar)
        {
            _session = session;
            _calendar = calendar;
            _storeId = calendar.StoreId;
        }

        public string Label => "Outlook: " + _calendar.Label;

        /// <summary>Переносятся файлы и вложенные элементы; ссылки (файл не в событии) и объекты OLE — нет.</summary>
        internal static bool IsCopied(int attachmentType) => attachmentType == OlByValue || attachmentType == OlEmbeddedItem;

        /// <summary>
        /// Имя вложения в копии. Вложенное письмо или событие Outlook сохраняет в формате .msg — у копии оно с этим
        /// расширением и открывается в Outlook двойным щелчком.
        /// </summary>
        internal static string AttachmentName(string? fileName, int attachmentType)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? "вложение" : fileName!.Trim();
            return attachmentType == OlEmbeddedItem && !name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) ? name + ".msg" : name;
        }

        /// <summary>Есть ли в RTF вставленная картинка (тогда описание переносится, даже если текста нет).</summary>
        internal static bool ContainsPicture(byte[] rtf)
        {
            string text = Encoding.ASCII.GetString(rtf);
            return text.IndexOf("\\pict", StringComparison.Ordinal) >= 0 || text.IndexOf("\\objdata", StringComparison.Ordinal) >= 0;
        }

        public Task<SourceRead> ReadAsync(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            IReadOnlyCollection<string> referencedSourceIds,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var referenced = new HashSet<string>(referencedSourceIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            DateTime from = TimeMapper.ToUtc(windowStartUtc) - Slack;
            DateTime to = TimeMapper.ToUtc(windowEndUtc) + Slack;
            int generation = Interlocked.Increment(ref _generation);
            return _session.RunAsync(
                ns =>
                {
                    List<Row> rows;
                    string storeId;
                    dynamic folder = OutlookSession.OpenFolder((object)ns, _calendar);
                    try
                    {
                        storeId = (string)folder.StoreID;
                        rows = ReadTable(folder, cancellationToken);
                    }
                    finally
                    {
                        Com.Release(folder);
                    }

                    // У события без глобального идентификатора копия помечена его EntryID — запрошенное ищется и по нему.
                    Row[] chosen = rows.Where(r => referenced.Contains(r.GlobalId) || referenced.Contains(r.EntryId) || TimeWindow.Overlaps(r.Start, r.SpanEnd, from, to)).ToArray();
                    var items = new List<SourceItem>(chosen.Length);
                    var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                    int unreadable = 0;
                    string? firstFailure = null;
                    var warnings = new List<string>();
                    foreach (Row row in chosen)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        StaWorker.Touch();
                        SourceItem? item = ReadItem((object)ns, row, storeId, warnings, out string? failure);
                        if (item is null)
                        {
                            unreadable++;
                            firstFailure = firstFailure ?? failure;
                            continue;
                        }

                        // Точное попадание — по полям события; запрошенные по идентификатору — всегда.
                        if (referenced.Contains(item.SourceId) || TimeWindow.Overlaps(item.StartUtc, item.SpanEndUtc, windowStartUtc, windowEndUtc))
                        {
                            items.Add(item);
                            entries[item.SourceId] = row.EntryId;
                        }

                        progress?.Report(items.Count);
                    }

                    lock (_sync)
                    {
                        if (generation == _generation)
                        {
                            _entryBySource = entries;
                            _storeId = storeId;
                        }
                    }

                    return new SourceRead(
                        items,
                        unreadable == 0 ? null : "Не прочитано событий Outlook: " + unreadable + ". Первое — " + firstFailure,
                        warnings);
                },
                cancellationToken);
        }

        public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            string? entryId;
            string storeId;
            lock (_sync)
            {
                _entryBySource.TryGetValue(item.SourceId, out entryId);
                storeId = _storeId;
            }

            if (entryId is null)
            {
                throw new InvalidOperationException("Событие Outlook не найдено среди прочитанных — постройте предпросмотр заново.");
            }

            return _session.RunAsync<IReadOnlyList<AttachmentContent>>(
                ns =>
                {
                    var list = new List<AttachmentContent>();
                    string temp = Path.Combine(Path.GetTempPath(), "iwo-attach-" + Guid.NewGuid().ToString("N"));
                    dynamic? appointment = null, attachments = null;
                    try
                    {
                        appointment = ns.GetItemFromID(entryId, storeId);
                        attachments = appointment.Attachments;
                        Directory.CreateDirectory(temp);
                        int count = (int)attachments.Count;
                        for (int i = 1; i <= count; i++)
                        {
                            dynamic attachment = attachments.Item(i);
                            try
                            {
                                int type = (int)attachment.Type;
                                if (IsCopied(type))
                                {
                                    // Outlook отдаёт содержимое только файлом: временный файл удаляется сразу после чтения.
                                    string path = Path.Combine(temp, i.ToString(CultureInfo.InvariantCulture));
                                    attachment.SaveAsFile(path);
                                    list.Add(new AttachmentContent(AttachmentName((string)attachment.FileName, type), File.ReadAllBytes(path)));
                                    File.Delete(path);
                                }
                            }
                            finally
                            {
                                Com.Release(attachment);
                            }

                            StaWorker.Touch();
                        }
                    }
                    finally
                    {
                        Com.Release(attachments);
                        Com.Release(appointment);
                        DeleteQuietly(temp);
                    }

                    return list;
                },
                cancellationToken);
        }

        private static void DeleteQuietly(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Антивирус держит файл — временная папка уберётся очисткой системы; исходная ошибка не подменяется.
            }
        }

        private static List<Row> ReadTable(dynamic folder, CancellationToken cancellationToken)
        {
            var rows = new List<Row>();
            dynamic? table = null, columns = null;
            try
            {
                table = folder.GetTable();
                columns = table.Columns;
                columns.RemoveAll();
                foreach (string property in new[] { "EntryID", StartProp, EndProp, RecurringProp, ClipEndProp, GlobalIdProp })
                {
                    Com.Release(columns.Add(property)); // Add возвращает объект столбца — он не нужен
                }

                bool marks = TryAddColumn(columns, BridgeMarkProp);
                while (!(bool)table.EndOfTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StaWorker.Touch();
                    dynamic row = table.GetNextRow();
                    try
                    {
                        // GetValues — значения всех столбцов одним вызовом (индексы с нуля в порядке добавления).
                        object[] values = (object[])row.GetValues();
                        bool isBridgeCopy = marks && values[6] is string mark && mark.Length > 0;
                        if (!isBridgeCopy && values[1] is DateTime s && values[2] is DateTime e)
                        {
                            bool recurring = values[3] is bool b && b;
                            DateTime spanEnd = recurring ? values[4] is DateTime clip ? TimeMapper.ToUtc(clip) : DateTime.MaxValue : TimeMapper.ToUtc(e);
                            string globalId = values[5] is null ? string.Empty : (string)row.BinaryToString(6);
                            rows.Add(new Row((string)values[0], globalId, TimeMapper.ToUtc(s), spanEnd));
                        }
                    }
                    finally
                    {
                        Com.Release(row);
                    }
                }
            }
            finally
            {
                Com.Release(columns);
                Com.Release(table);
            }

            return rows;
        }

        /// <summary>Столбец, которого может не быть в хранилище (метка моста): не добавился — читаем без него.</summary>
        private static bool TryAddColumn(dynamic columns, string property)
        {
            // Outlook сообщает о неподходящем столбце и COMException, и ArgumentException («Value does not fall within the
            // expected range», проверено вживую).
            try
            {
                Com.Release(columns.Add(property));
                return true;
            }
            catch (Exception ex) when ((ex is COMException || ex is ArgumentException) && !OutlookSession.IsFatal(ex))
            {
                return false;
            }
        }

        private static SourceItem? ReadItem(dynamic ns, Row row, string storeId, List<string> warnings, out string? failure)
        {
            failure = null;
            string step = "событие";
            dynamic? appointment = null;
            try
            {
                appointment = ns.GetItemFromID(row.EntryId, storeId);
                string subject = (string)appointment.Subject;
                string globalId = (string)appointment.GlobalAppointmentID;
                string sourceId = string.IsNullOrEmpty(globalId) ? row.EntryId : globalId;
                DateTime start = TimeMapper.ToUtc((DateTime)appointment.StartUTC);
                DateTime end = TimeMapper.ToUtc((DateTime)appointment.EndUTC);
                bool allDay = (bool)appointment.AllDayEvent;
                var busy = (FreeBusy)(int)appointment.BusyStatus;
                var sensitivity = (ItemSensitivity)(int)appointment.Sensitivity;

                step = "вложения";
                List<SyncAttachment> attachments = ReadAttachments((object)appointment, subject, warnings);

                // Описание с оформлением и картинками — RTF, как его хранит Outlook. Пустое описание (у Outlook это
                // всё равно RTF-заготовка в десятки КБ) не переносится: копия получит пустой текст.
                step = "описание";
                string body = (string)appointment.Body;
                byte[]? rtf = null;
                byte[]? raw = RtfBody((object)appointment);
                if (MapiRtf.LooksLikeRtf(raw) && (body.Trim().Length > 0 || ContainsPicture(raw!)))
                {
                    rtf = MapiRtf.FromRtf(raw!);
                }

                RecurrenceRule? rule = null;
                DateTime? seriesEnd = null;
                var exceptions = new List<SourceException>();
                if ((bool)appointment.IsRecurring)
                {
                    step = "правило серии";
                    (rule, seriesEnd) = ReadSeries((object)appointment, subject, end, exceptions, warnings);
                }

                return new SourceItem(
                    sourceId, subject, (string)appointment.Location, body, start, end, allDay, busy, sensitivity, rule, seriesEnd, exceptions, attachments, rtf);
            }
#pragma warning disable CA1031 // Непрочитанное событие делает чтение неполным (без удалений), но не обрывает его.
            catch (Exception ex) when (!OutlookSession.IsFatal(ex))
#pragma warning restore CA1031
            {
                // Сбой всего Outlook (закрыт, упал, занят) сюда не попадает: чтение прерывается целиком и повторяется после
                // переподключения, а не помечает все оставшиеся события непрочитанными.
                failure = step + ": " + ex.Message;
                return null;
            }
            finally
            {
                Com.Release(appointment);
            }
        }

        private static List<SyncAttachment> ReadAttachments(dynamic appointment, string subject, List<string> warnings)
        {
            var attachments = new List<SyncAttachment>();
            dynamic list = appointment.Attachments;
            try
            {
                int count = (int)list.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic attachment = list.Item(i);
                    try
                    {
                        int type = (int)attachment.Type;
                        if (IsCopied(type))
                        {
                            attachments.Add(new SyncAttachment(AttachmentName((string)attachment.FileName, type), (int)attachment.Size));
                        }
                        else
                        {
                            warnings.Add("«" + subject + "»: вложение «" + (string)attachment.DisplayName + "» — ссылка или встроенный объект OLE, в копию не переносится.");
                        }
                    }
                    finally
                    {
                        Com.Release(attachment);
                    }
                }
            }
            finally
            {
                Com.Release(list);
            }

            return attachments;
        }

        private static (RecurrenceRule Rule, DateTime? SeriesEnd) ReadSeries(
            dynamic appointment, string subject, DateTime end, List<SourceException> exceptions, List<string> warnings)
        {
            dynamic pattern = appointment.GetRecurrencePattern();
            dynamic? found = null;
            try
            {
                var p = new OutlookPattern
                {
                    RecurrenceType = (int)pattern.RecurrenceType,
                    Interval = (int)pattern.Interval,
                    DayOfWeekMask = (int)pattern.DayOfWeekMask,
                    DayOfMonth = (int)pattern.DayOfMonth,
                    MonthOfYear = (int)pattern.MonthOfYear,
                    Instance = (int)pattern.Instance,
                    PatternStartDate = (DateTime)pattern.PatternStartDate,
                    PatternEndDate = (DateTime)pattern.PatternEndDate,
                    NoEndDate = (bool)pattern.NoEndDate,
                };
                RecurrenceRule rule = OutlookRecurrenceMapper.ToRule(p);
                DateTime? seriesEnd = null;
                if (!p.NoEndDate)
                {
                    // Конец последнего повторения: его начало (дата окончания правила + время начала) плюс длительность —
                    // а не время конца в тот же день: у события через полночь и «на весь день» конец — на следующие сутки.
                    DateTime lastStartLocal = p.PatternEndDate.Date + ((DateTime)pattern.StartTime).TimeOfDay;
                    seriesEnd = LocalToUtc(lastStartLocal.AddMinutes((int)pattern.Duration));
                    if (seriesEnd < end)
                    {
                        seriesEnd = end;
                    }
                }

                found = pattern.Exceptions;
                int count = (int)found.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic exception = found.Item(i);
                    try
                    {
                        exceptions.Add(ReadException((object)exception, subject, warnings));
                    }
                    finally
                    {
                        Com.Release(exception);
                    }
                }

                return (rule, seriesEnd);
            }
            finally
            {
                Com.Release(found);
                Com.Release(pattern);
            }
        }

        private static SourceException ReadException(dynamic exception, string subject, List<string> warnings)
        {
            DateTime originalLocal = (DateTime)exception.OriginalDate;
            DateTime original = LocalToUtc(originalLocal);
            dynamic changed;
            try
            {
                if ((bool)exception.Deleted)
                {
                    return new SourceException(original, true);
                }

                changed = exception.AppointmentItem;
            }
            catch (COMException ex) when (!OutlookSession.IsFatal(ex))
            {
                // Повреждённое исключение серии: Outlook его не открывает ни через исключение, ни через GetOccurrence
                // (проверено вживую на общем календаре). Копия без него — как без удалённого повторения; остальная серия
                // переносится, человек видит замечание.
                warnings.Add("Серия «" + subject + "»: изменённое повторение от " + originalLocal.ToString("dd.MM.yyyy", Russian)
                    + " не открывается в Outlook — в копию не попадает.");
                return new SourceException(original, true);
            }

            try
            {
                int sensitivity = (int)changed.Sensitivity;
                return new SourceException(
                    original,
                    false,
                    TimeMapper.ToUtc((DateTime)changed.StartUTC),
                    TimeMapper.ToUtc((DateTime)changed.EndUTC),
                    (string)changed.Subject,
                    (string)changed.Location,
                    (string)changed.Body,
                    isPrivate: sensitivity == OlPrivate || sensitivity == OlConfidential);
            }
            finally
            {
                Com.Release(changed);
            }
        }

        /// <summary>
        /// Местное время Outlook → UTC. Время, которого в поясе компьютера не было (час перевода стрелок вперёд, например
        /// в старых сериях до 2011 года), сдвигается на час вперёд — так его показывает и Outlook, а не обрывает чтение серии.
        /// </summary>
        internal static DateTime LocalToUtc(DateTime local) => LocalToUtc(local, TimeZoneInfo.Local);

        internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
        {
            DateTime value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(value))
            {
                value = value.AddHours(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(value, zone);
        }

        /// <summary>RTF описания; <c>null</c> — Outlook 2007 (свойство RTFBody появилось в Outlook 2010) или описание недоступно.</summary>
        private static byte[]? RtfBody(dynamic appointment)
        {
            try
            {
                return appointment.RTFBody as byte[];
            }
#pragma warning disable CA1031 // Нет RTF — переносится текст описания. Сбой всего Outlook не глотается: копия без
            // оформления дала бы перезапись на этом прогоне и обратную на следующем.
            catch (Exception ex) when (!OutlookSession.IsFatal(ex))
#pragma warning restore CA1031
            {
                return null;
            }
        }

        private sealed class Row
        {
            public Row(string entryId, string globalId, DateTime start, DateTime spanEnd)
            {
                EntryId = entryId;
                GlobalId = globalId;
                Start = start;
                SpanEnd = spanEnd < start ? start : spanEnd;
            }

            public string EntryId { get; }

            public string GlobalId { get; }

            public DateTime Start { get; }

            public DateTime SpanEnd { get; }
        }
    }
}
