using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Reading;
using IwoBridge.InMemory;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Сценарий очистки целиком (предпросмотр → подтверждение → свежая сверка → удаление) поверх ин-мемори
    /// календаря, который повторяет важное поведение EWS. Манифест удаляемого проверяется отдельно —
    /// <see cref="CleanupRunnerManifestTests"/>.
    /// </summary>
    public sealed class CleanupRunnerTests
    {
        private static DateTime Utc(int year, int month, int day, int hour = 9, int minute = 0) =>
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        private static InMemoryOccurrence Occ(string id, DateTime start, bool modified = false) =>
            new InMemoryOccurrence(id, start, start.AddHours(1), modified);

        // Ин-мемори календарь реализует все четыре порта приёмника; манифест — двойник без файлов.
        private static CleanupRunner Runner(InMemoryCalendar calendar) =>
            new CleanupRunner(calendar, calendar, calendar, calendar, new RecordingManifestWriter());

        // Период: [10 янв 00:00 UTC, 20 янв 00:00 UTC).
        private static CleanupScope Period() => CleanupScope.Period(Utc(2026, 1, 10, 0), Utc(2026, 1, 20, 0));

        private static async Task<CleanupResult> PreviewAndRunAsync(
            CleanupRunner runner,
            CleanupScope scope,
            CleanupFilter filter,
            TargetDeleteMode mode = TargetDeleteMode.MoveToDeletedItems)
        {
            CleanupPreview preview = await runner.PreviewAsync(scope, filter, CancellationToken.None);
            Assert.True(preview.CanProceed, preview.BlockReason);
            return await runner.RunAsync(preview, mode, CancellationToken.None);
        }

        [Fact]
        public async Task WholeCalendar_DeletesAppointments_SkipsOtherClasses()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Single("s2", Utc(2026, 6, 1), Utc(2026, 6, 1, 10)),
                InMemoryEvent.Master("m1", "U1", new[] { Occ("m1-a", Utc(2026, 2, 1)), Occ("m1-b", Utc(2026, 2, 8)) }),
                InMemoryEvent.Single("n1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10), itemClass: "IPM.Note"),
            });

            CleanupResult result = await PreviewAndRunAsync(Runner(calendar), CleanupScope.WholeCalendar(), CleanupFilter.AllEvents);

            Assert.True(result.Succeeded);
            Assert.Equal(3, result.DeletedCount);
            Assert.Equal(0, result.TrimmedCount);
            Assert.Equal(new[] { "n1" }, calendar.ItemIds());
        }

        [Fact]
        public async Task Period_DeletesInside_TrimsBoundary_SkipsOutside()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s-in", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Single("s-out", Utc(2026, 1, 25), Utc(2026, 1, 25, 10)),
                InMemoryEvent.Master("m-inside", "U_IN", new[] { Occ("mi-1", Utc(2026, 1, 12)), Occ("mi-2", Utc(2026, 1, 16)) }),
                InMemoryEvent.Master("m-bound", "U_B", new[]
                {
                    Occ("mb-0", Utc(2026, 1, 8)), Occ("mb-1", Utc(2026, 1, 12)), Occ("mb-2", Utc(2026, 1, 18)), Occ("mb-3", Utc(2026, 1, 22)),
                }),
                InMemoryEvent.Master("m-out", "U_OUT", new[] { Occ("mo-1", Utc(2026, 1, 1)), Occ("mo-2", Utc(2026, 1, 5)) }),
            });

            CleanupResult result = await PreviewAndRunAsync(Runner(calendar), Period(), CleanupFilter.AllEvents);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.DeletedCount);  // s-in и m-inside
            Assert.Equal(2, result.TrimmedCount);  // mb-1 и mb-2
            Assert.Equal(new[] { "m-bound", "m-out", "s-out" }, calendar.ItemIds().OrderBy(x => x));
            Assert.Equal(new[] { "mb-0", "mb-3" }, calendar.OccurrenceIds("m-bound").OrderBy(x => x));
        }

        [Fact]
        public async Task Period_ExceptionMovedOutOfPeriod_SeriesKeptWithMovedException()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Master("m-x", "UX", new[]
                {
                    Occ("x-12", Utc(2026, 1, 12)), Occ("x-16", Utc(2026, 1, 16)), Occ("x-25", Utc(2026, 1, 25), modified: true),
                }),
            });

            CleanupResult result = await PreviewAndRunAsync(Runner(calendar), Period(), CleanupFilter.AllEvents);

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(2, result.TrimmedCount);
            Assert.Equal(new[] { "x-25" }, calendar.OccurrenceIds("m-x"));
        }

        [Fact]
        public async Task Period_InstanceEndingExactlyAtPeriodStart_NotDeleted()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Master("m-e", "UE", new[]
                {
                    new InMemoryOccurrence("e-edge", Utc(2026, 1, 9, 23), Utc(2026, 1, 10, 0)),
                    Occ("e-12", Utc(2026, 1, 12)),
                    Occ("e-22", Utc(2026, 1, 22)),
                }),
            });

            CleanupResult result = await PreviewAndRunAsync(Runner(calendar), Period(), CleanupFilter.AllEvents);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.TrimmedCount);
            Assert.Equal(new[] { "e-22", "e-edge" }, calendar.OccurrenceIds("m-e").OrderBy(x => x));
        }

        [Fact]
        public async Task BridgeCopiesOnly_ForeignSingleSharingSeriesUid_NotDeleted()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Master("mb", "UB", new[]
                {
                    Occ("b-08", Utc(2026, 1, 8)), Occ("b-12", Utc(2026, 1, 12)), Occ("b-22", Utc(2026, 1, 22)),
                }, bridgeSourceId: "src"),
                InMemoryEvent.Single("sf", Utc(2026, 1, 13), Utc(2026, 1, 13, 10), uid: "UB"),
            });

            CleanupResult result = await PreviewAndRunAsync(Runner(calendar), Period(), CleanupFilter.BridgeCopiesOnly);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.TrimmedCount);
            Assert.Contains("sf", calendar.ItemIds());
            Assert.Equal(new[] { "b-08", "b-22" }, calendar.OccurrenceIds("mb").OrderBy(x => x));
        }

        [Fact]
        public async Task BridgeCopiesOnly_WholeCalendar_DeletesOnlyMarked()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("mine", Utc(2026, 1, 15), Utc(2026, 1, 15, 10), bridgeSourceId: "src-1"),
                InMemoryEvent.Single("foreign", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
            });

            CleanupResult result = await PreviewAndRunAsync(
                Runner(calendar), CleanupScope.WholeCalendar(), CleanupFilter.BridgeCopiesOnly, TargetDeleteMode.HardDelete);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(new[] { "foreign" }, calendar.ItemIds());
        }

        [Fact]
        public async Task Preview_DoesNotDelete()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Single("s2", Utc(2026, 1, 16), Utc(2026, 1, 16, 10)),
            });

            CleanupPreview preview = await Runner(calendar).PreviewAsync(
                CleanupScope.WholeCalendar(), CleanupFilter.AllEvents, CancellationToken.None);

            Assert.True(preview.CanProceed);
            Assert.Equal(2, preview.Plan.DeleteCount);
            Assert.Equal(2, calendar.ItemIds().Count);
        }

        [Fact]
        public async Task Run_EventCreatedAfterPreview_NotTouched()
        {
            var calendar = new InMemoryCalendar(new[] { InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)) });
            CleanupRunner runner = Runner(calendar);
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);

            calendar.Add(InMemoryEvent.Single("s-new", Utc(2026, 1, 16), Utc(2026, 1, 16, 10)));
            CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(1, result.NewSincePreviewCount);
            Assert.Equal(new[] { "s-new" }, calendar.ItemIds());
        }

        [Fact]
        public async Task Run_EventRemovedAfterPreview_CountedAsChanged()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Single("s2", Utc(2026, 1, 16), Utc(2026, 1, 16, 10)),
            });
            CleanupRunner runner = Runner(calendar);
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);

            // Событие удалили с телефона между предпросмотром и удалением.
            await calendar.DeleteAsync(new[] { "s2" }, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);
            CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(1, result.ChangedSincePreviewCount);
            Assert.Empty(calendar.ItemIds());
        }

        [Fact]
        public async Task IncompleteSnapshot_PreviewBlocked_RunRefused_NothingDeleted()
        {
            var calendar = new InMemoryCalendar(new[] { InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)) });
            var runner = new CleanupRunner(new IncompletePageReader(), calendar, calendar, calendar, new RecordingManifestWriter());

            CleanupPreview preview = await runner.PreviewAsync(
                CleanupScope.WholeCalendar(), CleanupFilter.AllEvents, CancellationToken.None);
            CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);

            Assert.False(preview.CanProceed);
            Assert.NotNull(preview.BlockReason);
            Assert.True(result.Refused);
            Assert.False(result.Succeeded);
            Assert.Single(calendar.ItemIds());
        }

        [Fact]
        public async Task Progress_ReportsStagesInOrder_AndDeletionReachesTotal()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s-in", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Master("m-bound", "U_B", new[] { Occ("mb-1", Utc(2026, 1, 12)), Occ("mb-3", Utc(2026, 1, 22)) }),
            });
            CleanupRunner runner = Runner(calendar);
            var previewStages = new List<CleanupProgress>();
            var runStages = new List<CleanupProgress>();

            CleanupPreview preview = await runner.PreviewAsync(
                Period(), CleanupFilter.AllEvents, new SyncProgress<CleanupProgress>(previewStages.Add), CancellationToken.None);
            CleanupResult result = await runner.RunAsync(
                preview, TargetDeleteMode.MoveToDeletedItems, new SyncProgress<CleanupProgress>(runStages.Add), CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(
                new[] { CleanupStage.ReadingCalendar, CleanupStage.ReadingSeries, CleanupStage.ReadingPeriod },
                previewStages.Select(p => p.Stage).Distinct());
            Assert.Contains(previewStages, p => p.Stage == CleanupStage.ReadingCalendar && p.Completed == 2 && p.Total == 2);

            // Выполнение: свежая сверка (те же этапы чтения) → манифест → удаление до общего числа.
            Assert.Equal(
                new[]
                {
                    CleanupStage.ReadingCalendar, CleanupStage.ReadingSeries, CleanupStage.ReadingPeriod,
                    CleanupStage.WritingManifest, CleanupStage.Deleting,
                },
                runStages.Select(p => p.Stage).Distinct());
            CleanupProgress last = runStages.Last();
            Assert.Equal(CleanupStage.Deleting, last.Stage);
            Assert.Equal(2, last.Total);
            Assert.Equal(2, last.Completed);
        }

        [Fact]
        public async Task RunAsync_NullPreview_Throws()
        {
            var calendar = new InMemoryCalendar(Array.Empty<InMemoryEvent>());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Runner(calendar).RunAsync(null!, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None));
        }

        [Fact]
        public async Task Run_CancelDuringDeletion_StopsAfterBatch_ExactCounts()
        {
            var calendar = new InMemoryCalendar(new[]
            {
                InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)),
                InMemoryEvent.Single("s2", Utc(2026, 1, 16), Utc(2026, 1, 16, 10)),
                InMemoryEvent.Single("s3", Utc(2026, 1, 17), Utc(2026, 1, 17, 10)),
            });
            using (var cts = new CancellationTokenSource())
            {
                // Пакет по одному элементу; пользователь нажал «Остановить» во время первого пакета.
                var deleter = new CancelOnFirstDelete(calendar, cts);
                var runner = new CleanupRunner(
                    calendar, calendar, calendar, deleter, new RecordingManifestWriter(), executor: new CleanupExecutor(batchSize: 1));
                CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);

                CleanupResult result = await runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, cts.Token);

                Assert.True(result.Cancelled);
                Assert.False(result.Succeeded);
                Assert.False(result.Refused);
                Assert.Equal(1, result.DeletedCount);
                Assert.NotNull(result.ManifestPath);
                Assert.Equal(2, calendar.ItemIds().Count);
            }
        }

        [Fact]
        public async Task Run_CancelBeforeDeletion_Throws_NothingDeleted()
        {
            var calendar = new InMemoryCalendar(new[] { InMemoryEvent.Single("s1", Utc(2026, 1, 15), Utc(2026, 1, 15, 10)) });
            CleanupRunner runner = Runner(calendar);
            CleanupPreview preview = await runner.PreviewAsync(Period(), CleanupFilter.AllEvents, CancellationToken.None);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => runner.RunAsync(preview, TargetDeleteMode.MoveToDeletedItems, cts.Token));
            }

            Assert.Single(calendar.ItemIds());
        }

        /// <summary>Порт удаления, который «нажимает Остановить» во время первого пакета.</summary>
        private sealed class CancelOnFirstDelete : ITargetDeleter
        {
            private readonly ITargetDeleter _inner;
            private readonly CancellationTokenSource _cts;

            public CancelOnFirstDelete(ITargetDeleter inner, CancellationTokenSource cts)
            {
                _inner = inner;
                _cts = cts;
            }

            public Task<IReadOnlyList<Core.Paging.DeleteResult>> DeleteAsync(
                IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken)
            {
                _cts.Cancel();
                return _inner.DeleteAsync(itemIds, mode, cancellationToken);
            }
        }

        /// <summary>Неполный снимок: заявлено больше, чем отдано, последняя страница не помечена.</summary>
        private sealed class IncompletePageReader : ITargetPageReader
        {
            public Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
            {
                return Task.FromResult(new TargetPage(Array.Empty<TargetItemInfo>(), totalCount: 5, includesLastItemInRange: false));
            }
        }
    }
}
