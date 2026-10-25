using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Пакетное удаление: отказ без вызовов при неполном снимке, разбиение на пакеты, остановка на первом неполном пакете без маскировки частичного результата.
    /// </summary>
    public sealed class CleanupExecutorTests
    {
        private static IReadOnlyList<string> Ids(int count)
        {
            return Enumerable.Range(0, count).Select(i => "id" + i).ToList();
        }

        [Fact]
        public async Task NotAuthoritativeSnapshot_RefusesWithoutCalling()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());

            BatchDeleteOutcome outcome = await new CleanupExecutor().DeleteAllAsync(
                Ids(10), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: false, CancellationToken.None);

            Assert.False(outcome.IsSuccess);
            Assert.Empty(deleter.BatchSizes);
        }

        [Fact]
        public async Task Empty_SucceedsWithZero()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());

            BatchDeleteOutcome outcome = await new CleanupExecutor().DeleteAllAsync(
                Ids(0), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: true, CancellationToken.None);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(0, outcome.RemovedCount);
            Assert.Empty(deleter.BatchSizes);
        }

        [Fact]
        public async Task AllRemoved_BatchesBySize()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(120), deleter, TargetDeleteMode.HardDelete, snapshotIsAuthoritative: true, CancellationToken.None);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(120, outcome.RemovedCount);
            Assert.Equal(new[] { 50, 50, 20 }, deleter.BatchSizes);
        }

        [Fact]
        public async Task ItemRefused_OtherBatchesStillDeleted_FailureReported()
        {
            int batch = 0;
            var deleter = new FakeDeleter(ids =>
            {
                batch++;
                if (batch == 2)
                {
                    var list = ids.Select(_ => DeleteResult.Removed()).ToList();
                    list[0] = DeleteResult.Failed("ErrorAccessDenied");
                    return list;
                }

                return ids.Select(_ => DeleteResult.Removed()).ToList();
            });

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(150), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: true, CancellationToken.None);

            // Один неудаляемый элемент не держит остальную очистку: третий пакет удалён, отказ — в итоге, не маскируется.
            Assert.False(outcome.IsSuccess);
            Assert.True(outcome.ReachedEnd);
            Assert.Equal(1, outcome.RefusedCount);
            Assert.Equal(3, deleter.BatchSizes.Count);
            Assert.Equal(149, outcome.RemovedCount);
            Assert.Contains("ErrorAccessDenied", outcome.Error);
            Assert.Contains("1", outcome.Error);
        }

        [Fact]
        public async Task ResultCountMismatch_Stops()
        {
            int batch = 0;
            var deleter = new FakeDeleter(ids =>
            {
                batch++;
                return ids.Skip(batch == 1 ? 1 : 0).Select(_ => DeleteResult.Removed()).ToList();
            });

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(150), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: true, CancellationToken.None);

            Assert.False(outcome.IsSuccess);
            Assert.False(outcome.ReachedEnd);
            Assert.Single(deleter.BatchSizes);
        }

        [Fact]
        public async Task Progress_ReportsRunningTotalAfterEachBatch()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());
            var reported = new List<int>();

            await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(120), deleter, TargetDeleteMode.MoveToDeletedItems, true, new SyncProgress<int>(reported.Add), CancellationToken.None);

            Assert.Equal(new[] { 50, 100, 120 }, reported);
        }

        [Fact]
        public async Task CancelDuringBatch_FinishesBatch_ReturnsCancelledWithExactCount()
        {
            // Отмена во время пакета: пакет доводится до конца, следующий не начинается, счёт точный.
            using (var cts = new CancellationTokenSource())
            {
                var deleter = new FakeDeleter(ids =>
                {
                    cts.Cancel();
                    return ids.Select(_ => DeleteResult.Removed()).ToList();
                });
                var reported = new List<int>();

                BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                    Ids(120), deleter, TargetDeleteMode.MoveToDeletedItems, true, new SyncProgress<int>(reported.Add), cts.Token);

                Assert.True(outcome.IsCancelled);
                Assert.False(outcome.IsSuccess);
                Assert.Equal(50, outcome.RemovedCount);
                Assert.Equal(new[] { 50 }, deleter.BatchSizes);
                Assert.Equal(new[] { 50 }, reported);
            }
        }

        [Fact]
        public async Task PortThrows_FailureKeepsCountAndException()
        {
            // Обрыв сети на втором пакете: счёт удалённого не теряется, причина доступна для понятного сообщения.
            int batch = 0;
            var network = new System.Net.WebException("обрыв");
            var deleter = new FakeDeleter(ids =>
            {
                if (++batch == 2)
                {
                    throw network;
                }

                return ids.Select(_ => DeleteResult.Removed()).ToList();
            });

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(150), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: true, CancellationToken.None);

            Assert.False(outcome.IsSuccess);
            Assert.False(outcome.IsCancelled);
            Assert.Equal(50, outcome.RemovedCount);
            Assert.Same(network, outcome.Exception);
        }

        [Fact]
        public async Task ThrowingProgressReceiver_DoesNotAbortDeletion()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(120), deleter, TargetDeleteMode.MoveToDeletedItems, true,
                new SyncProgress<int>(_ => throw new InvalidOperationException("ошибка индикатора")), CancellationToken.None);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(120, outcome.RemovedCount);
        }

        [Fact]
        public async Task PortReturnsNull_FailureWithCount_NotCrash()
        {
            int batch = 0;
            var deleter = new FakeDeleter(ids => ++batch == 2 ? null! : ids.Select(_ => DeleteResult.Removed()).ToList());

            BatchDeleteOutcome outcome = await new CleanupExecutor(batchSize: 50).DeleteAllAsync(
                Ids(120), deleter, TargetDeleteMode.MoveToDeletedItems, true, CancellationToken.None);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(50, outcome.RemovedCount);
        }

        [Fact]
        public async Task CancelledBeforeStart_NothingSent_CancelledWithZero()
        {
            var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList());
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                BatchDeleteOutcome outcome = await new CleanupExecutor().DeleteAllAsync(
                    Ids(10), deleter, TargetDeleteMode.MoveToDeletedItems, snapshotIsAuthoritative: true, cts.Token);

                Assert.True(outcome.IsCancelled);
                Assert.Equal(0, outcome.RemovedCount);
                Assert.Empty(deleter.BatchSizes);
            }
        }

        [Fact]
        public async Task DeleterNeverReceivesCallerToken_StartedBatchIsNotAbandoned()
        {
            using (var cts = new CancellationTokenSource())
            {
                bool tokenCouldCancel = true;
                var deleter = new FakeDeleter(ids => ids.Select(_ => DeleteResult.Removed()).ToList(),
                    token => tokenCouldCancel = token.CanBeCanceled);

                await new CleanupExecutor().DeleteAllAsync(Ids(3), deleter, TargetDeleteMode.MoveToDeletedItems, true, cts.Token);

                Assert.False(tokenCouldCancel);
            }
        }

        /// <summary>Синхронный приёмник сообщений о ходе: порядок сообщений сохраняется (в отличие от Progress&lt;T&gt;).</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            public SyncProgress(Action<T> report) => _report = report;

            public void Report(T value) => _report(value);
        }

        private sealed class FakeDeleter : ITargetDeleter
        {
            private readonly Func<IReadOnlyList<string>, IReadOnlyList<DeleteResult>> _responder;
            private readonly Action<CancellationToken>? _onToken;

            public FakeDeleter(
                Func<IReadOnlyList<string>, IReadOnlyList<DeleteResult>> responder, Action<CancellationToken>? onToken = null)
            {
                _responder = responder;
                _onToken = onToken;
            }

            public List<int> BatchSizes { get; } = new List<int>();

            public Task<IReadOnlyList<DeleteResult>> DeleteAsync(
                IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken)
            {
                _onToken?.Invoke(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                BatchSizes.Add(itemIds.Count);
                return Task.FromResult(_responder(itemIds));
            }
        }
    }
}
