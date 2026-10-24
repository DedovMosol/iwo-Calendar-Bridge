using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using IwoBridge.Outlook;

namespace IwoBridge.Wpf.Services
{
    /// <summary>Календарь на выбор в списке «Outlook на этом компьютере».</summary>
    public sealed class SourceChoice
    {
        /// <param name="isCurrent">Этот календарь сейчас открыт в окне Outlook — выбирается по умолчанию.</param>
        /// <param name="key">Постоянный идентификатор (EntryID папки) — по нему выбор узнаётся после перезапуска; <c>null</c> — подпись.</param>
        public SourceChoice(string label, ISourceCalendar calendar, bool isCurrent = false, string? key = null)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
            IsCurrent = isCurrent;
            Key = string.IsNullOrEmpty(key) ? label : key!;
        }

        /// <summary>
        /// Чем календарь узнаётся при следующем чтении списка: подпись зависит от того, есть ли у Outlook окно (группы
        /// панели), а идентификатор папки — нет.
        /// </summary>
        public string Key { get; }

        public string Label { get; }

        public ISourceCalendar Calendar { get; }

        public bool IsCurrent { get; }
    }

    /// <summary>
    /// Календари Outlook этого компьютера — за портом, чтобы модель окна не зависела от COM и проверялась тестами.
    /// </summary>
    public interface IOutlookCalendars
    {
        /// <summary>Установлен ли Outlook (иначе вариант «Outlook» в окне недоступен).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Календари открытого Outlook, в том числе «Общие календари»; открытый сейчас помечен. Outlook не открыт —
        /// исключение с понятным текстом.
        /// </summary>
        Task<IReadOnlyList<SourceChoice>> ListAsync();

        /// <summary>Общий календарь по имени владельца (адресная книга Outlook). Не найден — исключение с понятным текстом.</summary>
        Task<SourceChoice> FindSharedAsync(string owner);
    }

    /// <summary>Настоящий Outlook через адаптер <see cref="OutlookSession"/>.</summary>
    public sealed class OutlookCalendars : IOutlookCalendars, IDisposable
    {
        private readonly OutlookSession _session = new OutlookSession();

        public bool IsAvailable => OutlookSession.IsInstalled;

        public async Task<IReadOnlyList<SourceChoice>> ListAsync()
        {
            IReadOnlyList<OutlookCalendarRef> calendars = await _session.ListCalendarsAsync().ConfigureAwait(true);
            return calendars.Select(c => new SourceChoice(c.Label, _session.Open(c), c.IsCurrent, c.EntryId)).ToArray();
        }

        public async Task<SourceChoice> FindSharedAsync(string owner)
        {
            OutlookCalendarRef calendar = await _session.FindSharedAsync(owner).ConfigureAwait(true);
            return new SourceChoice(calendar.Label, _session.Open(calendar), key: calendar.EntryId);
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }

    /// <summary>
    /// Выдуманные исходные календари демонстрационного режима: рабочий (встречи, серия, личное событие, вложение) и
    /// пустой. Тот же экран копирования без Outlook и сервера.
    /// </summary>
    public sealed class DemoSources : IOutlookCalendars
    {
        /// <summary>Выдуманный рабочий календарь — общий для списка «Outlook» и источника «по EWS» демонстрации.</summary>
        public static readonly InMemorySource Work = new InMemorySource("Петрова Анна Сергеевна — Общие календари (демонстрация)", Seed(DateTime.Today));

        public bool IsAvailable => true;

        public Task<IReadOnlyList<SourceChoice>> ListAsync()
        {
            IReadOnlyList<SourceChoice> list = new[]
            {
                new SourceChoice(Work.Label, Work, isCurrent: true),
                new SourceChoice("Отпуска — demo@example.test (демонстрация)", new InMemorySource("Отпуска (демонстрация)", Array.Empty<SourceItem>())),
            };
            return Task.FromResult(list);
        }

        /// <summary>Любое имя «находится» — пустым выдуманным общим календарём.</summary>
        public Task<SourceChoice> FindSharedAsync(string owner)
        {
            string label = owner.Trim() + " — общий календарь (демонстрация)";
            return Task.FromResult(new SourceChoice(label, new InMemorySource(label, Array.Empty<SourceItem>())));
        }

        private static IEnumerable<SourceItem> Seed(DateTime today)
        {
            DateTime At(int day, int hour) => DateTime.SpecifyKind(today.AddDays(day).AddHours(hour - 3), DateTimeKind.Utc);
            string[] subjects = { "Планёрка отдела", "Совещание по бюджету", "Встреча с подрядчиком", "Разбор заявок", "Обучение", "Отчёт за неделю" };
            for (int i = 0; i < 36; i++)
            {
                int day = -14 + i;
                yield return new SourceItem(
                    "DEMO-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                    subjects[i % subjects.Length] + (i >= subjects.Length ? " (" + (i / subjects.Length + 1) + ")" : string.Empty),
                    "Переговорная " + (i % 3 + 1),
                    "Повестка: пункты 1–3.",
                    At(day, 9 + i % 6),
                    At(day, 10 + i % 6));
            }

            yield return new SourceItem(
                "DEMO-SERIES", "Еженедельная сводка", "Зал 2", null, At(-7, 11), At(-7, 12),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, today.AddDays(-7), days: (WeekDays)(1 << (int)today.AddDays(-7).DayOfWeek)));
            yield return new SourceItem(
                "DEMO-PRIVATE", "Врач", "Поликлиника", "Анализы", At(2, 15), At(2, 16), sensitivity: ItemSensitivity.Private);
            yield return new SourceItem(
                "DEMO-FILE", "Защита проекта", "Зал 1", "Презентация во вложении.", At(5, 14), At(5, 15),
                attachments: new[] { new SyncAttachment("презентация.pptx", 120000) });
            yield return new SourceItem("DEMO-ALLDAY", "Выездной день", null, null, At(9, 3), At(10, 3), isAllDay: true, busy: FreeBusy.OutOfOffice);
        }
    }
}
