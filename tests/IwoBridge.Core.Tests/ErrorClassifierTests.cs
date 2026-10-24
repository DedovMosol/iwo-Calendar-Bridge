using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Errors;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Классификация ошибок и повторы: аутентификация не повторяется никогда, временные — с пределом и растущей
    /// задержкой либо по подсказке сервера, постоянные и конфликт версии — без повторов; каждый вид классифицирован.
    /// </summary>
    public sealed class ErrorClassifierTests
    {
        [Theory]
        [InlineData(OperationErrorKind.Timeout)]
        [InlineData(OperationErrorKind.ConnectionReset)]
        [InlineData(OperationErrorKind.ServerBusy)]
        [InlineData(OperationErrorKind.ComBusy)]
        public void Transient(OperationErrorKind kind)
        {
            Assert.Equal(ErrorCategory.Transient, ErrorClassifier.Classify(kind));
        }

        [Theory]
        [InlineData(OperationErrorKind.AccessDenied)]
        [InlineData(OperationErrorKind.MailboxNotFound)]
        [InlineData(OperationErrorKind.EndpointNotFound)]
        [InlineData(OperationErrorKind.QuotaExceeded)]
        [InlineData(OperationErrorKind.RequestTooLarge)]
        [InlineData(OperationErrorKind.SchemaError)]
        [InlineData(OperationErrorKind.CertificatePinMismatch)]
        [InlineData(OperationErrorKind.TlsNegotiationFailed)]
        [InlineData(OperationErrorKind.Unknown)]
        public void Permanent(OperationErrorKind kind)
        {
            Assert.Equal(ErrorCategory.Permanent, ErrorClassifier.Classify(kind));
        }

        [Fact]
        public void EveryErrorKind_IsClassified()
        {
            // Защита от добавления нового вида ошибки без правила: классификатор бросает на неизвестном значении.
            foreach (OperationErrorKind kind in Enum.GetValues(typeof(OperationErrorKind)))
            {
                Assert.True(Enum.IsDefined(typeof(ErrorCategory), ErrorClassifier.Classify(kind)));
            }
        }

        // ---- RetryPolicy ----

        private sealed class KindException : Exception
        {
            public KindException(OperationErrorKind kind, TimeSpan? backOff = null)
            {
                Kind = kind;
                BackOff = backOff;
            }

            public OperationErrorKind Kind { get; }

            public TimeSpan? BackOff { get; }
        }

        private static RetryPolicy Policy(List<TimeSpan> waits, int maxRetries = 3) => new RetryPolicy(
            ex => ex is KindException k ? k.Kind : OperationErrorKind.Unknown,
            ex => (ex as KindException)?.BackOff,
            maxRetries,
            (d, ct) =>
            {
                waits.Add(d);
                return Task.CompletedTask;
            });

        private static Func<Task<int>> FailThen(int failures, Func<Exception> error, Action? onCall = null)
        {
            int calls = 0;
            return () =>
            {
                onCall?.Invoke();
                if (calls++ < failures)
                {
                    throw error();
                }

                return Task.FromResult(42);
            };
        }

        [Fact]
        public async Task Retry_Transient_SucceedsAfterFailures_WithDoublingDelay()
        {
            var waits = new List<TimeSpan>();

            int result = await Policy(waits).ExecuteAsync(FailThen(2, () => new KindException(OperationErrorKind.ServerBusy)), CancellationToken.None);

            Assert.Equal(42, result);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, waits);
        }

        [Fact]
        public async Task Retry_ServerBackOffHint_Used_AndCapped()
        {
            var waits = new List<TimeSpan>();

            await Policy(waits).ExecuteAsync(
                FailThen(2, () => new KindException(OperationErrorKind.ServerBusy, TimeSpan.FromMinutes(10))), CancellationToken.None);

            Assert.Equal(new[] { RetryPolicy.MaxDelay, RetryPolicy.MaxDelay }, waits);
        }

        [Fact]
        public async Task Retry_GivesUpAfterMax_RethrowsOriginal()
        {
            var waits = new List<TimeSpan>();
            int calls = 0;

            var ex = await Assert.ThrowsAsync<KindException>(() => Policy(waits, maxRetries: 2).ExecuteAsync(
                FailThen(10, () => new KindException(OperationErrorKind.Timeout), () => calls++), CancellationToken.None));

            Assert.Equal(OperationErrorKind.Timeout, ex.Kind);
            Assert.Equal(3, calls);
        }

        [Theory]
        [InlineData(OperationErrorKind.AuthenticationFailed)]
        [InlineData(OperationErrorKind.AccessDenied)]
        [InlineData(OperationErrorKind.VersionConflict)]
        [InlineData(OperationErrorKind.Unknown)]
        public async Task Retry_NonTransient_NeverRetried(OperationErrorKind kind)
        {
            // Аутентификация — главное: повтор заблокировал бы учётную запись доменной политикой.
            var waits = new List<TimeSpan>();
            int calls = 0;

            await Assert.ThrowsAsync<KindException>(() => Policy(waits).ExecuteAsync(
                FailThen(1, () => new KindException(kind), () => calls++), CancellationToken.None));

            Assert.Equal(1, calls);
            Assert.Empty(waits);
        }

        [Fact]
        public async Task Retry_CancelledDuringWait_Stops()
        {
            using (var cts = new CancellationTokenSource())
            {
                int calls = 0;
                var policy = new RetryPolicy(
                    _ => OperationErrorKind.Timeout,
                    maxRetries: 5,
                    delay: (d, ct) =>
                    {
                        cts.Cancel();
                        return Task.Delay(d, ct);
                    });

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync(
                    FailThen(10, () => new TimeoutException(), () => calls++), cts.Token));

                Assert.Equal(1, calls);
            }
        }

        [Fact]
        public async Task Retry_OperationCanceled_NotRetried()
        {
            var waits = new List<TimeSpan>();
            int calls = 0;
            var policy = new RetryPolicy(_ => OperationErrorKind.Timeout, maxRetries: 3, delay: (d, ct) =>
            {
                waits.Add(d);
                return Task.CompletedTask;
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => policy.ExecuteAsync(
                FailThen(10, () => new OperationCanceledException(), () => calls++), CancellationToken.None));

            Assert.Equal(1, calls);
        }

        [Fact]
        public void Retry_NegativeMax_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(_ => OperationErrorKind.Unknown, maxRetries: -1));
        }
    }
}
