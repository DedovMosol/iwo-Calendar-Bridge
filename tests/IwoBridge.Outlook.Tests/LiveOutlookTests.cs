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
                IReadOnlyList<OutlookCalendarRef> calendars = await session.ListCalendarsAsync();
                foreach (OutlookCalendarRef c in calendars)
                {
                    _output.WriteLine((c.IsCurrent ? "* " : "  ") + c.Label);
                }

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
                _output.WriteLine("Найден: " + found.Label);
                await ReadTwice(session.Open(found));
            }
        }

        private async Task ReadTwice(ISourceCalendar source)
        {
            DateTime today = DateTime.UtcNow.Date;
            DateTime from = today.AddMonths(-1), to = today.AddMonths(3);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SourceRead read = await WithLimit(source.ReadAsync(from, to, Array.Empty<string>(), null, CancellationToken.None));
            IReadOnlyList<SourceItem> items = read.Items;
            _output.WriteLine(
                $"{source.Label}: событий {items.Count}, серий {items.Count(i => i.Recurrence != null)}, исключений {items.Sum(i => i.Exceptions.Count)}, "
                + $"весь день {items.Count(i => i.IsAllDay)}, личных {items.Count(i => i.Sensitivity == ItemSensitivity.Private)}, "
                + $"с вложениями {items.Count(i => i.Attachments.Count > 0)}; неполное: {read.IncompleteReason ?? "нет"}; {watch.Elapsed.TotalSeconds:0.0} с");
            foreach (string warning in read.Warnings)
            {
                _output.WriteLine("  замечание: " + warning);
            }

            foreach (SourceItem i in items.OrderBy(i => i.StartUtc).Take(5))
            {
                _output.WriteLine($"  {i.StartUtc.ToLocalTime():dd.MM.yyyy HH:mm}–{i.EndUtc.ToLocalTime():HH:mm} {i.Subject}{(i.Recurrence != null ? " (серия " + i.Recurrence.Canonical + ")" : string.Empty)}");
            }

            Assert.Null(read.IncompleteReason);
            Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.SourceId)));
            Assert.All(items, i => Assert.True(i.EndUtc >= i.StartUtc));
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
