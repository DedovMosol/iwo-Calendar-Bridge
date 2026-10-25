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
        private readonly ActiveEndpoint _active;

        /// <param name="active">
        /// Номер рабочего адреса, общий с другим каналом того же подключения (чтение и запись): когда один канал перешёл на
        /// резервный адрес, второй сразу идёт туда же, а не ждёт тайм-аута на недоступном основном. <c>null</c> — свой.
        /// </param>
        public EwsChannel(IReadOnlyList<ExchangeService> services, RetryPolicy? retry, ActiveEndpoint? active = null)
        {
            if (services is null || services.Count == 0 || services.Any(s => s is null))
            {
                throw new ArgumentException("Нужен хотя бы один сервис EWS.", nameof(services));
            }

            _services = services.ToArray();
            _retry = retry ?? RetryPolicy.None;
            _active = active ?? new ActiveEndpoint();
        }

        /// <summary>Номер адреса, через который сейчас идут запросы (0 — основной).</summary>
        public int ActiveIndex => _active.Index;

        /// <summary>Сервис текущего адреса — для создания объектов EWS (новое событие), а не для вызовов.</summary>
        public ExchangeService Current => _services[ActiveIndex];

        /// <param name="retry">Повторять ли временные ошибки (проверка подключения — нет: человек ждёт ответа).</param>
        public Task<T> RunAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken, bool retry = true)
        {
            return retry
                ? _retry.ExecuteAsync(() => RunOnceAsync(work, cancellationToken, idempotent: true), cancellationToken)
                : RunOnceAsync(work, cancellationToken, idempotent: true);
        }

        /// <summary>
        /// Неидемпотентная запись (создание события, добавление вложения): без повторов, на другой адрес — только если
        /// запрос точно не ушёл (<see cref="EwsErrorMapper.IsNotSent"/>). После обрыва или тайм-аута ответа сервер мог
        /// запрос выполнить, и повтор создал бы второй экземпляр.
        /// </summary>
        public Task<T> RunOnceNonIdempotentAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken)
        {
            return RunOnceAsync(work, cancellationToken, idempotent: false);
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
        private async Task<T> RunOnceAsync<T>(Func<ExchangeService, T> work, CancellationToken cancellationToken, bool idempotent)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int index = _active.Index % _services.Length;
                for (int tried = 1; ; tried++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExchangeService service = _services[index];
                    try
                    {
                        T result = await System.Threading.Tasks.Task.Run(() => work(service)).ConfigureAwait(false);
                        _active.Index = index;
                        return result;
                    }
                    catch (Exception ex) when (tried < _services.Length
                        && !cancellationToken.IsCancellationRequested
                        && (idempotent ? ErrorClassifier.IsUnreachable(EwsErrorMapper.MapException(ex)) : EwsErrorMapper.IsNotSent(ex)))
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

    /// <summary>Номер рабочего адреса подключения; запись и чтение атомарны (общий для каналов одного подключения).</summary>
    internal sealed class ActiveEndpoint
    {
        private int _index;

        public int Index
        {
            get => Volatile.Read(ref _index);
            set => Volatile.Write(ref _index, value);
        }
    }
}
