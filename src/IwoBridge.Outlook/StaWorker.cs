using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IwoBridge.Outlook
{
    /// <summary>
    /// Отдельный поток STA для всех вызовов Outlook (PLAN.md §9). Объектная модель Outlook однопоточна: вызовы из
    /// пула потоков дают RPC_E_WRONG_THREAD и зависания. Поток один на сеанс, вызовы идут по очереди.
    /// На потоке зарегистрирован фильтр сообщений COM: «Outlook занят» (RPC_E_CALL_REJECTED, SERVERCALL_RETRYLATER —
    /// например, открыт диалог или идёт синхронизация) не обрывает чтение, а повторяется через 250 мс, не дольше
    /// минуты.
    /// </summary>
    internal sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public StaWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Outlook (STA)" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> RunAsync<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        done.SetResult(work());
                    }
#pragma warning disable CA1031 // Исключение доставляется вызывающему через задачу.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        done.SetException(ex);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                done.SetException(new ObjectDisposedException(nameof(StaWorker)));
            }

            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }

        private void Loop()
        {
            MessageFilter.Register();
            try
            {
                foreach (Action action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            }
            finally
            {
                MessageFilter.Revoke();
                _queue.Dispose();
            }
        }

        /// <summary>Фильтр сообщений COM: повтор вызова, который Outlook отклонил как «занят».</summary>
        private sealed class MessageFilter : IOleMessageFilter
        {
            private const int Retry = 250;
            private const int GiveUpAfterMs = 60000;
            private const int ServerCallRetryLater = 2;

            public static void Register()
            {
                CoRegisterMessageFilter(new MessageFilter(), out _);
            }

            public static void Revoke()
            {
                CoRegisterMessageFilter(null, out _);
            }

            int IOleMessageFilter.HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) => 0;

            int IOleMessageFilter.RetryRejectedCall(IntPtr callee, int tickCount, int rejectType)
            {
                return rejectType == ServerCallRetryLater && tickCount < GiveUpAfterMs ? Retry : -1;
            }

            int IOleMessageFilter.MessagePending(IntPtr callee, int tickCount, int pendingType) => 2;

            [DllImport("Ole32.dll")]
            private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter oldFilter);
        }

        [ComImport]
        [Guid("00000016-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleMessageFilter
        {
            [PreserveSig]
            int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);

            [PreserveSig]
            int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);

            [PreserveSig]
            int MessagePending(IntPtr callee, int tickCount, int pendingType);
        }
    }
}
