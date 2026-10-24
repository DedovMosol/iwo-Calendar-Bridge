using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Errors;
using IwoBridge.Ews.Mapping;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Канал вызовов EWS одного ящика — единое место трёх правил, общих для очистки и копирования (DRY):
    ///  - в полёте не больше одного запроса (семафор): безопасность <see cref="ExchangeService"/> при параллельном
    ///    использовании не гарантируется, а NTLM привязан к соединению;
    ///  - адрес недоступен (<see cref="ErrorClassifier.IsUnreachable"/>) — тот же запрос сразу по следующему адресу,
    ///    дальше работа через него; ошибки входа, прав и сертификата адрес не меняют;
    ///  - временные ошибки повторяются (<see cref="RetryPolicy"/>) снаружи перехода: недоступный адрес не держит работу
    ///    на время всех попыток с паузами. Пауза — вне семафора.
    /// Отмена кооперативная: ожидание очереди прерывается сразу, начатый запрос (он не отменяем) доводится до конца.
    /// </summary>
    internal sealed class EwsChannel
    {
        private readonly ExchangeService[] _services;
        private readonly RetryPolicy _retry;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private int _active;

        public EwsChannel(IReadOnlyList<ExchangeService> services, RetryPolicy? retry)
        {
            if (services is null || services.Count == 0 || services.Any(s => s is null))
            {
                throw new ArgumentException("Нужен хотя бы один сервис EWS.", nameof(services));
            }

            _services = services.ToArray();
            _retry = retry ?? RetryPolicy.None;
        }

        /// <summary>Номер адреса, через который сейчас идут запросы (0 — основной).</summary>
        public int ActiveIndex => Volatile.Read(ref _active);

        /// <summary>Сервис текущего адреса — для создания объектов EWS (новое событие), а не для вызовов.</summary>
        public ExchangeService Current => _services[ActiveIndex];

        /// <param name="retry">Повторять ли временные ошибки (проверка подключения — нет: человек ждёт ответа).</param>
        public Task<T> RunAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken, bool retry = true)
        {
            return retry
                ? _retry.ExecuteAsync(() => RunOnceAsync(work, cancellationToken), cancellationToken)
                : RunOnceAsync(work, cancellationToken);
        }

        public Task RunAsync(Action<ExchangeService> work, CancellationToken cancellationToken)
        {
            return RunAsync<bool>(
                service =>
                {
                    work(service);
                    return true;
                },
                cancellationToken);
        }

        /// <summary>
        /// Попытка по текущему адресу; недоступен — сразу по следующему (каждый адрес не больше одного раза за попытку).
        /// Удачный адрес становится текущим. Номер адреса меняется только под семафором — гонок нет.
        /// </summary>
        private async Task<T> RunOnceAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int index = _active;
                for (int tried = 1; ; tried++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExchangeService service = _services[index];
                    try
                    {
                        T result = await System.Threading.Tasks.Task.Run(() => work(service)).ConfigureAwait(false);
                        Volatile.Write(ref _active, index);
                        return result;
                    }
                    catch (Exception ex) when (tried < _services.Length
                        && !cancellationToken.IsCancellationRequested
                        && ErrorClassifier.IsUnreachable(EwsErrorMapper.MapException(ex)))
                    {
                        index = (index + 1) % _services.Length;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
