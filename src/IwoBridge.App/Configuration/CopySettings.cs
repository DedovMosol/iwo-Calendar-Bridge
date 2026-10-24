using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using IwoBridge.Core.Sync;

namespace IwoBridge.App.Configuration
{
    /// <summary>
    /// Что и как копировать — запоминается между запусками (<c>config\copy.json</c>), чтобы автокопирование продолжалось
    /// после перезапуска программы или Windows. Учётных данных здесь нет: они в подключениях.
    /// Период автокопирования — скользящий: столько-то дней назад и вперёд от текущей даты.
    /// </summary>
    public sealed class CopySettings
    {
        /// <summary>Допустимые интервалы автокопирования, минуты.</summary>
        public static readonly int[] Intervals = { 5, 15, 30, 60, 120, 240, 480, 1440 };

        public const int MaxDays = 3660;

        public CopySettings(
            bool fromOutlook = true,
            string? outlookCalendar = null,
            PrivateEvents privateEvents = PrivateEvents.HideDetails,
            bool autoCopy = false,
            int intervalMinutes = 30,
            int daysBack = 30,
            int daysForward = 92,
            string? outlookOwner = null,
            string? outlookCalendarKey = null)
        {
            OutlookCalendarKey = string.IsNullOrWhiteSpace(outlookCalendarKey) ? null : outlookCalendarKey;
            FromOutlook = fromOutlook;
            OutlookOwner = string.IsNullOrWhiteSpace(outlookOwner) ? null : outlookOwner!.Trim();
            OutlookCalendar = string.IsNullOrWhiteSpace(outlookCalendar) ? null : outlookCalendar!.Trim();
            PrivateEvents = Enum.IsDefined(typeof(PrivateEvents), privateEvents) ? privateEvents : PrivateEvents.HideDetails;
            AutoCopy = autoCopy;
            IntervalMinutes = Array.IndexOf(Intervals, intervalMinutes) >= 0 ? intervalMinutes : 30;
            DaysBack = Clamp(daysBack);
            DaysForward = Clamp(daysForward);
        }

        /// <summary>Источник — Outlook этого компьютера (иначе — подключение «Источник по EWS»).</summary>
        public bool FromOutlook { get; }

        /// <summary>Название календаря Outlook (как в списке) или имя владельца общего календаря.</summary>
        public string? OutlookCalendar { get; }

        /// <summary>
        /// Как был найден общий календарь (ФИО или адрес, введённые человеком), если его нет в панели Outlook: по этому
        /// имени он находится заново после перезапуска.
        /// </summary>
        public string? OutlookOwner { get; }

        /// <summary>Идентификатор папки календаря Outlook (EntryID) — по нему выбор узнаётся; подпись — запасной путь.</summary>
        public string? OutlookCalendarKey { get; }

        public PrivateEvents PrivateEvents { get; }

        public bool AutoCopy { get; }

        public int IntervalMinutes { get; }

        public int DaysBack { get; }

        public int DaysForward { get; }

        /// <summary>Период автокопирования на дату <paramref name="today"/>: первый и последний день включительно.</summary>
        public (DateTime First, DateTime Last) Window(DateTime today) => (today.Date.AddDays(-DaysBack), today.Date.AddDays(DaysForward));

        private static int Clamp(int days) => Math.Max(0, Math.Min(MaxDays, days));
    }

    /// <summary>
    /// Файл <see cref="CopySettings"/>. Повреждённый или чужой файл не мешает работе: читаются значения по умолчанию
    /// (настройки копирования восстановимы — человек выберет их заново), запись — атомарная, как у профиля.
    /// </summary>
    public static class CopySettingsFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static CopySettings Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new CopySettings();
                }

                using (var stream = File.OpenRead(path))
                {
                    if (!(new DataContractJsonSerializer(typeof(Document)).ReadObject(stream) is Document d))
                    {
                        return new CopySettings();
                    }

                    return new CopySettings(
                        d.Source != "ews",
                        d.OutlookCalendar,
                        d.PrivateEvents == "all" ? PrivateEvents.CopyAll : d.PrivateEvents == "skip" ? PrivateEvents.Skip : PrivateEvents.HideDetails,
                        d.AutoCopy,
                        d.IntervalMinutes,
                        d.DaysBack ?? 30,
                        d.DaysForward ?? 92,
                        d.OutlookOwner,
                        d.OutlookCalendarKey);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException || ex is XmlException
                || ex is ArgumentException || ex is NotSupportedException)
            {
                return new CopySettings();
            }
        }

        /// <exception cref="IOException">Нет места, файл занят.</exception>
        /// <exception cref="UnauthorizedAccessException">Нет прав на запись в папку настроек.</exception>
        public static void Save(string path, CopySettings settings)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var document = new Document
            {
                Source = settings.FromOutlook ? "outlook" : "ews",
                OutlookCalendar = settings.OutlookCalendar,
                PrivateEvents = settings.PrivateEvents == PrivateEvents.CopyAll ? "all" : settings.PrivateEvents == PrivateEvents.Skip ? "skip" : "hide",
                AutoCopy = settings.AutoCopy,
                IntervalMinutes = settings.IntervalMinutes,
                DaysBack = settings.DaysBack,
                DaysForward = settings.DaysForward,
                OutlookOwner = settings.OutlookOwner,
                OutlookCalendarKey = settings.OutlookCalendarKey,
            };

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (XmlDictionaryWriter writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Utf8NoBom, ownsStream: false, indent: true))
                {
                    new DataContractJsonSerializer(typeof(Document)).WriteObject(writer, document);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                FileSafety.ReplaceWith(tempPath, fullPath);
            }
            finally
            {
                FileSafety.DeleteQuietly(tempPath);
            }
        }

        [DataContract]
        private sealed class Document
        {
            [DataMember(Name = "source", Order = 1)]
            public string? Source { get; set; }

            [DataMember(Name = "outlookCalendar", Order = 2)]
            public string? OutlookCalendar { get; set; }

            [DataMember(Name = "privateEvents", Order = 3)]
            public string? PrivateEvents { get; set; }

            [DataMember(Name = "autoCopy", Order = 4)]
            public bool AutoCopy { get; set; }

            [DataMember(Name = "intervalMinutes", Order = 5)]
            public int IntervalMinutes { get; set; }

            [DataMember(Name = "daysBack", Order = 6)]
            public int? DaysBack { get; set; }

            [DataMember(Name = "daysForward", Order = 7)]
            public int? DaysForward { get; set; }

            [DataMember(Name = "outlookOwner", Order = 8)]
            public string? OutlookOwner { get; set; }

            [DataMember(Name = "outlookCalendarId", Order = 9)]
            public string? OutlookCalendarKey { get; set; }
        }
    }
}
