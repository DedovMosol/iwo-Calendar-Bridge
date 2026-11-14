using System;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.InMemory;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>Предпросмотр серии: в столбце даты — первое вхождение в периоде, не 1949.</summary>
    public sealed class CopyTextsTests
    {
        [Fact]
        public async Task BuildRows_ClippedSeries_ShowsWindowOccurrence_Not1949()
        {
            TargetTimeZone zone = TargetTimeZone.FromHours(3);
            DateTime first = zone.LocalToUtc(new DateTime(1949, 3, 16, 8, 0, 0));
            var item = new SourceItem(
                "old-series",
                "Планёрка",
                null,
                null,
                first,
                first.AddHours(1),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, new DateTime(1949, 3, 16), days: WeekDays.Wednesday));
            var runner = new CopyRunner(new InMemorySource("источник", new[] { item }), new InMemoryCopyTarget());
            DateTime from = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
            DateTime to = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

            CopyPreview preview = await runner.PreviewAsync(from, to, false, null, CancellationToken.None);
            CopyRow row = Assert.Single(CopyTexts.BuildRows(preview, zone));

            Assert.StartsWith("07.10.2026 08:00 (серия)", row.Start, StringComparison.Ordinal);
            Assert.Contains("в источнике с 16.03.1949", row.Start, StringComparison.Ordinal);
            Assert.Contains("серия", row.Start, StringComparison.Ordinal);
        }
    }
}
