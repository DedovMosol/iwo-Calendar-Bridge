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
    ///
    /// Сторож зависаний: вызов, который не вернулся за отведённое время (Outlook показал окно и ждёт человека, «не
    /// отвечает»), завершается для вызывающего ошибкой <see cref="TimeoutException"/>, а поток помечается испорченным
    /// (<see cref="IsBroken"/>) — владелец заводит новый. Остановить зависший вызов COM нельзя; фоновой поток с ним
    /// дождётся Outlook или конца процесса и выходу программы не мешает. Отмена (<see cref="CancellationToken"/>)
    /// снимает ожидание сразу: задача, поставленная в очередь, но ещё не начатая, не выполняется вовсе.
    /// </summary>
    internal sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private int _broken;

        public StaWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Outlook (STA)" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Вызов завис дольше отведённого — поток занят им, новые вызовы надо отдавать новому потоку.</summary>
        public bool IsBroken => Volatile.Read(ref _broken) != 0;

        public Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                done.SetCanceled();
                return done.Task;
            }

            // Сторож: таймер и отмена только завершают задачу вызывающего; сам вызов COM прервать нельзя.
            var watchdog = new Timer(
                _ =>
                {
                    if (done.TrySetException(new TimeoutException(
                        "Outlook не отвечает дольше " + (int)timeout.TotalMinutes + " мин. Возможно, в нём открыто окно с вопросом — ответьте на него.")))
                    {
                        Interlocked.Exchange(ref _broken, 1);
                    }
                },
                null,
                timeout,
                Timeout.InfiniteTimeSpan);
            CancellationTokenRegistration cancel = cancellationToken.Register(() => done.TrySetCanceled());
            done.Task.ContinueWith(
                _ =>
                {
                    watchdog.Dispose();
                    cancel.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                _queue.Add(() =>
                {
                    if (done.Task.IsCompleted)
                    {
                        return; // отменено, пока ждало в очереди
                    }

                    try
                    {
                        done.TrySetResult(work());
                    }
#pragma warning disable CA1031 // Исключение доставляется вызывающему через задачу.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        done.TrySetException(ex);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                done.TrySetException(new ObjectDisposedException(nameof(StaWorker)));
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

    /// <summary>Освобождение объектов COM Outlook — одно место для сеанса и источника (DRY).</summary>
    internal static class Com
    {
        /// <summary>
        /// Освободить RCW сразу, не дожидаясь сборщика мусора: в режиме «онлайн» Exchange ограничивает число открытых
        /// объектов (около 250), и программа, работающая неделями, не должна их копить.
        /// </summary>
        public static void Release(object? com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
            }
        }
    }
}
