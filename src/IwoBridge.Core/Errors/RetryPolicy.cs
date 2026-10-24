using System;
using System.Threading;
using System.Threading.Tasks;

namespace IwoBridge.Core.Errors
{
    /// <summary>
    /// Повтор временных ошибок сервера — единое место политики. Повторяются только ошибки
    /// категории <see cref="ErrorCategory.Transient"/> (таймаут, обрыв, «сервер занят»); аутентификация не
    /// повторяется никогда, постоянные ошибки и конфликт версии — тоже (их повтор без перечитывания
    /// бессмыслен). Задержка растёт вдвое (1, 2, 4 с …, не больше <see cref="MaxDelay"/>) либо берётся из подсказки
    /// сервера (BackOffMilliseconds у Exchange 2010+), если она есть.
    ///
    /// Повторять безопасно: чтения ничего не меняют, а удаление идемпотентно — уже удалённое при повторе
    /// возвращается как «уже отсутствует». Отмена прерывает и ожидание между попытками.
    ///
    /// Класс без состояния между вызовами; потокобезопасен.
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>Потолок задержки между попытками.</summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

        private readonly Func<Exception, OperationErrorKind> _classify;
        private readonly Func<Exception, TimeSpan?> _serverBackOff;
        private readonly int _maxRetries;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <param name="classify">Вид ошибки по исключению адаптера.</param>
        /// <param name="serverBackOff">Задержка, которую просит сервер; <c>null</c> — подсказок нет.</param>
        /// <param name="maxRetries">Сколько раз повторить после первой неудачи (0 — без повторов).</param>
        /// <param name="delay">Ожидание (в тестах — мгновенное); по умолчанию <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
        public RetryPolicy(
            Func<Exception, OperationErrorKind> classify,
            Func<Exception, TimeSpan?>? serverBackOff = null,
            int maxRetries = 3,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            if (maxRetries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "Число повторов не может быть отрицательным.");
            }

            _classify = classify ?? throw new ArgumentNullException(nameof(classify));
            _serverBackOff = serverBackOff ?? (_ => null);
            _maxRetries = maxRetries;
            _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        }

        /// <summary>Политика без повторов — для тестов и мест, где повтор не нужен.</summary>
        public static RetryPolicy None { get; } = new RetryPolicy(_ => OperationErrorKind.Unknown, maxRetries: 0);

        /// <summary>Выполняет операцию, повторяя её при временных ошибках. Прочие ошибки — сразу наружу.</summary>
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            TimeSpan nextDelay = FirstDelay;
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < _maxRetries && !(ex is OperationCanceledException) && IsTransient(ex))
                {
                    TimeSpan wait = Clamp(_serverBackOff(ex) ?? nextDelay);
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                    nextDelay = Clamp(TimeSpan.FromTicks(nextDelay.Ticks * 2));
                }
            }
        }

        private bool IsTransient(Exception exception)
        {
            return ErrorClassifier.Classify(_classify(exception)) == ErrorCategory.Transient;
        }

        private static TimeSpan Clamp(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return value > MaxDelay ? MaxDelay : value;
        }
    }
}
