using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IwoBridge.Outlook.Tests
{
    /// <summary>
    /// Поток STA для Outlook: сторож зависаний. Долгий, но продвигающийся вызов не считается зависшим; остановившийся —
    /// считается; вызов в очереди за зависшим не ждёт вечно. Outlook не нужен.
    /// </summary>
    public sealed class StaWorkerTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

        [Fact]
        public async Task LongCall_WithProgress_NotTimedOut()
        {
            using (var worker = new StaWorker())
            {
                int result = await worker.RunAsync(
                    () =>
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            Thread.Sleep(100);
                            StaWorker.Touch();
                        }

                        return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 1 : 0;
                    },
                    Short,
                    CancellationToken.None);

                Assert.Equal(1, result);
                Assert.False(worker.IsBroken);
            }
        }

        [Fact]
        public async Task StalledCall_TimesOut_WorkerBroken()
        {
            using (var release = new ManualResetEventSlim())
            using (var worker = new StaWorker())
            {
                Task<int> stalled = worker.RunAsync(() => { release.Wait(); return 0; }, Short, CancellationToken.None);

                await Assert.ThrowsAsync<TimeoutException>(() => stalled);
                Assert.True(worker.IsBroken);
                release.Set();
            }
        }

        [Fact]
        public async Task QueuedBehindStalled_TimesOutToo_NeverRuns()
        {
            using (var release = new ManualResetEventSlim())
            using (var worker = new StaWorker())
            {
                Task<int> stalled = worker.RunAsync(() => { release.Wait(); return 0; }, TimeSpan.FromSeconds(30), CancellationToken.None);
                bool ran = false;
                Task<int> queued = worker.RunAsync(() => { ran = true; return 1; }, Short, CancellationToken.None);

                await Assert.ThrowsAsync<TimeoutException>(() => queued);
                release.Set();
                await stalled;
                await Task.Delay(100);
                Assert.False(ran);
            }
        }

        [Fact]
        public void Touch_OutsideWorker_DoesNothing()
        {
            StaWorker.Touch();
        }
    }
}
