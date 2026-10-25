using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using Xunit;
using Xunit.Abstractions;

namespace IwoBridge.Outlook.Tests
{
    /// <summary>
    /// Живая проверка источника «Outlook» на этом компьютере — только чтение. Выполняется, если задана переменная
    /// окружения <c>IWO_LIVE_OUTLOOK=1</c> и Outlook запущен (иначе пропускается — в CI его нет):
    ///  - список календарей (в том числе «Общие календари») и отметка открытого сейчас;
    ///  - полное чтение открытого календаря за месяц назад — три месяца вперёд, повторное чтение даёт те же отпечатки;
    ///  - <c>IWO_LIVE_OUTLOOK_FIND</c> (необязательно) — поиск общего календаря по имени владельца.
    /// </summary>
    public sealed class LiveOutlookTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
        private readonly ITestOutputHelper _output;

        public LiveOutlookTests(ITestOutputHelper output) => _output = output;

        [LiveOutlookFact]
        public async Task CurrentCalendar_Listed_ReadsCompletely_Idempotent()
        {
            using (var session = new OutlookSession())
            {
                // Только числа: названия календарей и темы событий — служебные данные, в вывод тестов они не попадают.
                IReadOnlyList<OutlookCalendarRef> calendars = await session.ListCalendarsAsync();
                _output.WriteLine("Календарей в Outlook: " + calendars.Count + ", открыт сейчас: " + calendars.Count(c => c.IsCurrent));

                OutlookCalendarRef current = calendars.Single(c => c.IsCurrent);
                await ReadTwice(session.Open(current));
            }
        }

        [LiveOutlookFact]
        public async Task FindShared_ByOwnerName()
        {
            string? owner = Environment.GetEnvironmentVariable("IWO_LIVE_OUTLOOK_FIND");
            if (string.IsNullOrEmpty(owner))
            {
                return;
            }

            using (var session = new OutlookSession())
            {
                OutlookCalendarRef found = await session.FindSharedAsync(owner!);
                _output.WriteLine("Общий календарь найден.");
                await ReadTwice(session.Open(found));
            }
        }

        private async Task ReadTwice(ISourceCalendar source)
        {
            DateTime today = DateTime.UtcNow.Date;
            DateTime from = today.AddMonths(-1), to = today.AddMonths(3);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int fallbacks = OutlookSource.KeywordsFallbacks;
            SourceRead read = await WithLimit(source.ReadAsync(from, to, Array.Empty<string>(), null, CancellationToken.None));
            IReadOnlyList<SourceItem> items = read.Items;

            // Категории читаются точным списком (PidNameKeywords), а не разбором строки: запасной путь не понадобился.
            Assert.Equal(fallbacks, OutlookSource.KeywordsFallbacks);
            _output.WriteLine(
                $"событий {items.Count}, серий {items.Count(i => i.Recurrence != null)}, исключений {items.Sum(i => i.Exceptions.Count)}, "
                + $"весь день {items.Count(i => i.IsAllDay)}, личных {items.Count(i => i.Sensitivity == ItemSensitivity.Private)}, "
                + $"с вложениями {items.Count(i => i.Attachments.Count > 0)}, с оформлением {items.Count(i => i.RtfCompressed != null)}, "
                + $"с категориями {items.Count(i => i.Categories.Count > 0)}, повторений со своими категориями {items.Sum(i => i.Exceptions.Count(e => e.Categories != null))}, "
                + $"категорий с цветом {EventCategories.Used(items, read.CategoryColors).Count(c => c.Color.HasValue)} из {EventCategories.Used(items, null).Count}, "
                + $"замечаний {read.Warnings.Count}; неполное: {(read.IncompleteReason is null ? "нет" : "да")}; {watch.Elapsed.TotalSeconds:0.0} с");

            // Причина неполного чтения содержит тему события — в сообщение проверки не попадает.
            Assert.True(read.IncompleteReason is null, "чтение неполное");
            Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.SourceId)));
            Assert.All(items, i => Assert.True(i.EndUtc >= i.StartUtc));
            Assert.All(items, i => Assert.All(i.Categories, c => Assert.False(string.IsNullOrWhiteSpace(c))));
            Assert.Equal(items.Count, items.Select(i => i.SourceId).Distinct(StringComparer.Ordinal).Count());

            // Повторное чтение даёт те же отпечатки: копирование из Outlook идемпотентно.
            SourceRead again = await WithLimit(source.ReadAsync(from, to, Array.Empty<string>(), null, CancellationToken.None));
            Assert.Equal(
                items.Select(i => i.SourceId + "=" + i.ContentHash).OrderBy(s => s, StringComparer.Ordinal),
                again.Items.Select(i => i.SourceId + "=" + i.ContentHash).OrderBy(s => s, StringComparer.Ordinal));

            SourceItem? withFile = items.FirstOrDefault(i => i.Attachments.Count > 0);
            if (withFile != null)
            {
                IReadOnlyList<AttachmentContent> files = await WithLimit(source.LoadAttachmentsAsync(withFile, CancellationToken.None));
                Assert.Equal(withFile.Attachments.Count, files.Count);
            }
        }

        private static async Task<T> WithLimit<T>(Task<T> work)
        {
            Task finished = await Task.WhenAny(work, Task.Delay(Limit));
            Assert.True(ReferenceEquals(finished, work), "Outlook не ответил за " + Limit.TotalMinutes + " мин.");
            return await work;
        }
    }

    /// <summary>Живая проверка Outlook: выполняется только при <c>IWO_LIVE_OUTLOOK=1</c>.</summary>
    public sealed class LiveOutlookFactAttribute : FactAttribute
    {
        public LiveOutlookFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("IWO_LIVE_OUTLOOK") != "1")
            {
                Skip = "Живая проверка Outlook выключена (IWO_LIVE_OUTLOOK).";
            }
        }
    }
}
