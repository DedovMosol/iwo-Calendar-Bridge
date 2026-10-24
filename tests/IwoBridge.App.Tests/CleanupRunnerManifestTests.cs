using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.InMemory;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Манифест удаляемого в сценарии очистки: пишется до первого удаления, совпадает со списком удаления,
    /// а если записать его нельзя — ничего не удаляется (PLAN.md §8, T30).
    /// </summary>
    public sealed class CleanupRunnerManifestTests
    {
        private static DateTime Utc(int month, int day, int hour = 9) => new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

        private static InMemoryOccurrence Occ(string id, DateTime start, bool modified = false, string? subject = null) =>
            new InMemoryOccurrence(id, start, start.AddHours(1), modified, subject);

        // Период: [10 янв 00:00 UTC, 20 янв 00:00 UTC).
        private static CleanupScope Period() => CleanupScope.Period(Utc(1, 10, 0), Utc(1, 20, 0));

        /// <summary>
        /// Календарь со всеми видами удаляемого: одиночное в периоде, серия целиком в периоде, серия на границе
        /// (два экземпляра в периоде, один из них — исключение со своей темой) и одиночное вне периода.
        /// </summary>
        private static InMemoryCalendar MixedCalendar() => new InMemoryCalendar(new[]
        {
            InMemoryEvent.Single("s-in", Utc(1, 15), Utc(1, 15, 10), subject: "Планёрка"),
            InMemoryEvent.Single("s-out", Utc(1, 25), Utc(1, 25, 10), subject: "Вне периода"),
            InMemoryEvent.Master("m-in", "U_IN", new[] { Occ("mi-1", Utc(1, 12)), Occ("mi-2", Utc(1, 16)) }, subject: "Серия внутри"),
            InMemoryEvent.Master("m-bound", "U_B", new[]
            {
                Occ("mb-0", Utc(1, 8)),
                Occ("mb-1", Utc(1, 12)),
                Occ("mb-2", Utc(1, 18), modified: true, subject: "Перенесено"),
                Occ("mb-3", Utc(1, 22)),
            }, subject: "Граничная серия"),
        });

        private static async Task<(CleanupPreview Preview, CleanupResult Result)> PreviewAndRunAsync(CleanupRunner runner)
        {
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);
            Assert.True(preview.CanProceed, preview.BlockReason);
            CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);
            return (preview, result);
        }

        [Fact]
        public async Task Run_WritesManifestBeforeDeleting_ExactlyTheDeletionList()
        {
            InMemoryCalendar calendar = MixedCalendar();
            IReadOnlyList<string> itemsAtWrite = Array.Empty<string>();
            IReadOnlyList<string> boundaryAtWrite = Array.Empty<string>();
            var manifest = new RecordingManifestWriter(onWrite: () =>
            {
                // Снимок календаря в момент записи манифеста: удаление ещё не должно было начаться.
                itemsAtWrite = calendar.ItemIds();
                boundaryAtWrite = calendar.OccurrenceIds("m-bound");
            });
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, manifest);

            (_, CleanupResult result) = await PreviewAndRunAsync(runner);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(new[] { "m-bound", "m-in", "s-in", "s-out" }, itemsAtWrite.OrderBy(x => x));
            Assert.Equal(4, boundaryAtWrite.Count);

            IReadOnlyList<CleanupManifestEntry> entries = Assert.Single(manifest.Writes);
            Assert.Equal(
                new[]
                {
                    (CleanupManifestEntryKind.Single, "s-in", "Планёрка"),
                    (CleanupManifestEntryKind.WholeSeries, "m-in", "Серия внутри"),
                    (CleanupManifestEntryKind.SeriesInstance, "mb-1", "Граничная серия"),
                    (CleanupManifestEntryKind.SeriesInstance, "mb-2", "Перенесено"),
                },
                entries.Select(e => (e.Kind, e.ItemId, e.Subject ?? string.Empty)));

            // Удалено ровно перечисленное в манифесте.
            Assert.Equal(entries.Count, result.DeletedCount + result.TrimmedCount);
            Assert.Equal(new[] { "m-bound", "s-out" }, calendar.ItemIds().OrderBy(x => x));
            Assert.Equal(new[] { "mb-0", "mb-3" }, calendar.OccurrenceIds("m-bound").OrderBy(x => x));
            Assert.Equal("memory://manifest-1", result.ManifestPath);
        }

        [Fact]
        public async Task Run_ManifestRowTimes_ComeFromDeletedElements()
        {
            InMemoryCalendar calendar = MixedCalendar();
            var manifest = new RecordingManifestWriter();
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, manifest);

            await PreviewAndRunAsync(runner);

            IReadOnlyList<CleanupManifestEntry> entries = Assert.Single(manifest.Writes);
            CleanupManifestEntry single = entries.Single(e => e.ItemId == "s-in");
            Assert.Equal(Utc(1, 15), single.StartUtc);
            Assert.Equal(Utc(1, 15, 10), single.EndUtc);

            // Серия целиком — время первого повторения; экземпляр — своё время.
            Assert.Equal(Utc(1, 12), entries.Single(e => e.ItemId == "m-in").StartUtc);
            Assert.Equal(Utc(1, 18), entries.Single(e => e.ItemId == "mb-2").StartUtc);
        }

        [Fact]
        public async Task Run_EventCreatedAfterPreview_NotInManifest()
        {
            InMemoryCalendar calendar = MixedCalendar();
            var manifest = new RecordingManifestWriter();
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, manifest);
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);

            calendar.Add(InMemoryEvent.Single("s-new", Utc(1, 16), Utc(1, 16, 10), subject: "Новое"));
            CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.DoesNotContain(Assert.Single(manifest.Writes), e => e.ItemId == "s-new");
            Assert.Contains("s-new", calendar.ItemIds());
        }

        [Theory]
        [InlineData("io")]
        [InlineData("access")]
        public async Task Run_ManifestNotWritten_RefusedAndNothingDeleted(string failureKind)
        {
            InMemoryCalendar calendar = MixedCalendar();
            Exception failure = failureKind == "io"
                ? new IOException("Недостаточно места на диске.")
                : (Exception)new UnauthorizedAccessException("Нет доступа к папке.");
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, new RecordingManifestWriter(failure));

            (_, CleanupResult result) = await PreviewAndRunAsync(runner);

            Assert.True(result.Refused);
            Assert.False(result.Succeeded);
            Assert.Contains("Манифест", result.Message);
            Assert.Null(result.ManifestPath);
            Assert.Equal(4, calendar.ItemIds().Count);
            Assert.Equal(4, calendar.OccurrenceIds("m-bound").Count);
        }

        [Fact]
        public async Task Run_UnexpectedManifestError_Propagates_NothingDeleted()
        {
            // Ошибка программы (не отказ среды) не маскируется отказом, но и до удаления дело не доходит.
            InMemoryCalendar calendar = MixedCalendar();
            var failing = new RecordingManifestWriter(new InvalidOperationException("Ошибка программы."));
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, failing);
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None));

            Assert.Equal(4, calendar.ItemIds().Count);
        }

        [Fact]
        public async Task Run_NothingToDelete_NoManifest()
        {
            var calendar = new InMemoryCalendar(new[] { InMemoryEvent.Single("s-out", Utc(1, 25), Utc(1, 25, 10)) });
            var manifest = new RecordingManifestWriter();
            var runner = new CleanupRunner(calendar, calendar, calendar, calendar, manifest);

            (_, CleanupResult result) = await PreviewAndRunAsync(runner);

            Assert.True(result.Succeeded);
            Assert.Empty(manifest.Writes);
            Assert.Null(result.ManifestPath);
            Assert.Equal(new[] { "s-out" }, calendar.ItemIds());
        }

        [Fact]
        public void Constructor_NullManifestWriter_Throws()
        {
            var calendar = new InMemoryCalendar(Array.Empty<InMemoryEvent>());

            Assert.Throws<ArgumentNullException>(() => new CleanupRunner(calendar, calendar, calendar, calendar, null!));
        }
    }
}
