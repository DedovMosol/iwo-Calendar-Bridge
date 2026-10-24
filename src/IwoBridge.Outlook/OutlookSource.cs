using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    ///     начало, конец, признак серии и конец серии. По ней отбираются события, пересекающие период (с запасом в сутки),
    ///     и запрошенные по идентификатору.
    ///  2. Отобранные события открываются целиком: поля, правило серии, исключения, имена вложений; точная проверка
    ///     попадания в период — по полям самого события.
    /// Идентификатор события — GlobalAppointmentID (не меняется при переносе в другую папку и совпадает с UID, который
    /// видит Exchange); если его нет — EntryID. Встреча копируется простым событием: участники не переносятся.
    /// Каждый объект COM освобождается сразу после использования: в режиме «онлайн» Exchange ограничивает число
    /// открытых объектов (около 250), и без этого чтение тысяч событий обрывалось бы.
    /// </summary>
    internal sealed class OutlookSource : ISourceCalendar
    {
        // Свойства MAPI по пространству имён: в таблице их даты приходят в UTC (а по обычным именам — в местном времени).
        private const string StartProp = "urn:schemas:calendar:dtstart";
        private const string EndProp = "urn:schemas:calendar:dtend";
        private const string RecurringProp = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/8223000B";
        private const string ClipEndProp = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/82360040";
        private const string GlobalIdProp = "http://schemas.microsoft.com/mapi/id/{6ED8DA90-450B-101B-98DA-00AA003F1305}/00030102";

        private const int OlByReference = 4;

        // Запас при отборе по таблице: точное сравнение — по полям события.
        private static readonly TimeSpan Slack = TimeSpan.FromDays(1);

        private readonly OutlookSession _session;
        private readonly OutlookCalendarRef _calendar;
        private readonly object _sync = new object();
        private Dictionary<string, string> _entryBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        public OutlookSource(OutlookSession session, OutlookCalendarRef calendar)
        {
            _session = session;
            _calendar = calendar;
        }

        public string Label => "Outlook: " + _calendar.Label;

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
            return _session.RunAsync(ns =>
            {
                dynamic folder = OutlookSession.OpenFolder((object)ns, _calendar);
                List<Row> rows = ReadTable(folder);
                Release(folder);

                Row[] chosen = rows.Where(r => referenced.Contains(r.GlobalId) || TimeWindow.Overlaps(r.Start, r.SpanEnd, from, to)).ToArray();
                var items = new List<SourceItem>(chosen.Length);
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                int unreadable = 0;
                string? firstFailure = null;
                var warnings = new List<string>();
                foreach (Row row in chosen)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SourceItem? item = ReadItem((object)ns, row, _calendar.StoreId, warnings, out string? failure);
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
                    _entryBySource = entries;
                }

                return new SourceRead(
                    items,
                    unreadable == 0 ? null : "Не прочитано событий Outlook: " + unreadable + ". Первое — " + firstFailure,
                    warnings);
            });
        }

        public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            string? entryId;
            lock (_sync)
            {
                _entryBySource.TryGetValue(item.SourceId, out entryId);
            }

            if (entryId is null)
            {
                throw new InvalidOperationException("Событие Outlook не найдено среди прочитанных — постройте предпросмотр заново.");
            }

            return _session.RunAsync<IReadOnlyList<AttachmentContent>>(ns =>
            {
                dynamic appointment = ns.GetItemFromID(entryId, _calendar.StoreId);
                var list = new List<AttachmentContent>();
                dynamic attachments = appointment.Attachments;
                string temp = Path.Combine(Path.GetTempPath(), "iwo-attach-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    int count = (int)attachments.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic attachment = attachments.Item(i);
                        if ((int)attachment.Type != OlByReference)
                        {
                            string name = (string)attachment.FileName;
                            string path = Path.Combine(temp, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            attachment.SaveAsFile(path);
                            list.Add(new AttachmentContent(name, File.ReadAllBytes(path)));
                            File.Delete(path);
                        }

                        Release(attachment);
                    }
                }
                finally
                {
                    Release(attachments);
                    Release(appointment);
                    try
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                    catch (IOException)
                    {
                        // Временная папка уберётся очисткой системы.
                    }
                }

                return list;
            });
        }

        private static List<Row> ReadTable(dynamic folder)
        {
            var rows = new List<Row>();
            dynamic table = folder.GetTable();
            dynamic columns = table.Columns;
            columns.RemoveAll();
            columns.Add("EntryID");
            columns.Add(StartProp);
            columns.Add(EndProp);
            columns.Add(RecurringProp);
            columns.Add(ClipEndProp);
            columns.Add(GlobalIdProp);
            while (!(bool)table.EndOfTable)
            {
                dynamic row = table.GetNextRow();
                try
                {
                    // GetValues — значения всех столбцов одним вызовом (индексы с нуля в порядке добавления).
                    object[] values = (object[])row.GetValues();
                    if (values[1] is DateTime s && values[2] is DateTime e)
                    {
                        bool recurring = values[3] is bool b && b;
                        DateTime spanEnd = recurring ? values[4] is DateTime clip ? TimeMapper.ToUtc(clip) : DateTime.MaxValue : TimeMapper.ToUtc(e);
                        string globalId = values[5] is null ? string.Empty : (string)row.BinaryToString(6);
                        rows.Add(new Row((string)values[0], globalId, TimeMapper.ToUtc(s), spanEnd));
                    }
                }
                finally
                {
                    Release(row);
                }
            }

            Release(columns);
            Release(table);
            return rows;
        }

        private static SourceItem? ReadItem(dynamic ns, Row row, string storeId, List<string> warnings, out string? failure)
        {
            failure = null;
            string step = "событие";
            dynamic? appointment = null;
            try
            {
                appointment = ns.GetItemFromID(row.EntryId, storeId);
                string globalId = (string)appointment.GlobalAppointmentID;
                string sourceId = string.IsNullOrEmpty(globalId) ? row.EntryId : globalId;
                DateTime start = TimeMapper.ToUtc((DateTime)appointment.StartUTC);
                DateTime end = TimeMapper.ToUtc((DateTime)appointment.EndUTC);
                bool allDay = (bool)appointment.AllDayEvent;
                var busy = (FreeBusy)(int)appointment.BusyStatus;
                var sensitivity = (ItemSensitivity)(int)appointment.Sensitivity;
                var attachments = new List<SyncAttachment>();
                dynamic list = appointment.Attachments;
                int count = (int)list.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic attachment = list.Item(i);
                    if ((int)attachment.Type != OlByReference)
                    {
                        attachments.Add(new SyncAttachment((string)attachment.FileName, (int)attachment.Size));
                    }

                    Release(attachment);
                }

                Release(list);

                RecurrenceRule? rule = null;
                DateTime? seriesEnd = null;
                var exceptions = new List<SourceException>();
                if ((bool)appointment.IsRecurring)
                {
                    step = "правило серии";
                    dynamic pattern = appointment.GetRecurrencePattern();
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
                    rule = OutlookRecurrenceMapper.ToRule(p);
                    if (!p.NoEndDate)
                    {
                        // Конец последнего повторения: дата окончания правила + время конца события (местное время Outlook).
                        DateTime lastEndLocal = p.PatternEndDate.Date + ((DateTime)pattern.EndTime).TimeOfDay;
                        seriesEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(lastEndLocal, DateTimeKind.Unspecified), TimeZoneInfo.Local);
                        if (seriesEnd < end)
                        {
                            seriesEnd = end;
                        }
                    }

                    dynamic found = pattern.Exceptions;
                    int exceptionCount = (int)found.Count;
                    for (int i = 1; i <= exceptionCount; i++)
                    {
                        dynamic exception = found.Item(i);
                        DateTime original = TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind((DateTime)exception.OriginalDate, DateTimeKind.Unspecified), TimeZoneInfo.Local);
                        if ((bool)exception.Deleted)
                        {
                            exceptions.Add(new SourceException(original, true));
                        }
                        else
                        {
                            step = "повторение серии";
                            dynamic? changed = null;
                            try
                            {
                                changed = exception.AppointmentItem;
                            }
                            catch (COMException)
                            {
                                // Повреждённое исключение серии: Outlook его не открывает ни через исключение, ни через
                                // GetOccurrence (проверено вживую на общем календаре). Копия без него — как без удалённого
                                // повторения; остальная серия переносится, человек видит замечание.
                                exceptions.Add(new SourceException(original, true));
                                warnings.Add("Серия «" + (string)appointment.Subject + "»: изменённое повторение от "
                                    + ((DateTime)exception.OriginalDate).ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("ru-RU"))
                                    + " не открывается в Outlook — в копию не попадает.");
                            }

                            if (changed != null)
                            {
                                exceptions.Add(new SourceException(
                                    original,
                                    false,
                                    TimeMapper.ToUtc((DateTime)changed.StartUTC),
                                    TimeMapper.ToUtc((DateTime)changed.EndUTC),
                                    (string)changed.Subject,
                                    (string)changed.Location,
                                    (string)changed.Body));
                                Release(changed);
                            }
                        }

                        Release(exception);
                    }

                    Release(found);
                    Release(pattern);
                }

                return new SourceItem(
                    sourceId,
                    (string)appointment.Subject,
                    (string)appointment.Location,
                    (string)appointment.Body,
                    start,
                    end,
                    allDay,
                    busy,
                    sensitivity,
                    rule,
                    seriesEnd,
                    exceptions,
                    attachments);
            }
#pragma warning disable CA1031 // Непрочитанное событие делает чтение неполным (без удалений), но не обрывает его.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                string subject = string.Empty;
                try
                {
                    subject = appointment is null ? string.Empty : " «" + (string)appointment!.Subject + "»";
                }
#pragma warning disable CA1031 // Тема для сообщения — по возможности.
                catch (Exception)
#pragma warning restore CA1031
                {
                }

                failure = step + subject + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (appointment != null)
                {
                    Release(appointment);
                }
            }
        }

        private static void Release(object? com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
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
