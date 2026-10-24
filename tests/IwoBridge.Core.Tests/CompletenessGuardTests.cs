using System.Collections.Generic;
using IwoBridge.Core.Paging;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Строгая оценка полноты: перечисление полно только при последней странице и совпадении числа; пакетное удаление успешно только при учёте всех id («уже нет» — успех).
    /// </summary>
    public sealed class CompletenessGuardTests
    {
        [Fact]
        public void Paging_LastPageAndCountMatch_IsComplete()
        {
            Completeness result = CompletenessGuard.EvaluatePaging(
                totalItemsInView: 1500, distinctItemsCollected: 1500, includesLastItemInRange: true);

            Assert.True(result.IsComplete);
        }

        [Fact]
        public void Paging_LastPageNotReached_IsIncomplete()
        {
            Completeness result = CompletenessGuard.EvaluatePaging(1500, 1500, includesLastItemInRange: false);

            Assert.False(result.IsComplete);
            Assert.NotNull(result.Reason);
        }

        [Fact]
        public void Paging_CountMismatch_IsIncomplete()
        {
            Completeness result = CompletenessGuard.EvaluatePaging(1500, 1499, includesLastItemInRange: true);

            Assert.False(result.IsComplete);
        }

        [Fact]
        public void BatchDelete_Empty_SucceedsWithZero()
        {
            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(new List<DeleteResult>(), 0);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(0, outcome.RemovedCount);
        }

        [Fact]
        public void BatchDelete_AllRemoved_Succeeds()
        {
            var results = new List<DeleteResult> { DeleteResult.Removed(), DeleteResult.Removed() };

            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, 2);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(2, outcome.RemovedCount);
        }

        [Fact]
        public void BatchDelete_AlreadyAbsent_IsIdempotentSuccess()
        {
            var results = new List<DeleteResult> { DeleteResult.Removed(), DeleteResult.AlreadyAbsent() };

            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, 2);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(2, outcome.RemovedCount);
        }

        [Fact]
        public void BatchDelete_PartialFailure_IsNotMasked()
        {
            var results = new List<DeleteResult>
            {
                DeleteResult.Removed(),
                DeleteResult.AlreadyAbsent(),
                DeleteResult.Failed("ErrorAccessDenied"),
            };

            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, 3);

            Assert.False(outcome.IsSuccess);
            Assert.Contains("Частично удалено 2 из 3", outcome.Error);
            Assert.Equal(2, outcome.RemovedCount);
        }

        [Fact]
        public void BatchDelete_ResultCountMismatch_IsFailure()
        {
            var results = new List<DeleteResult> { DeleteResult.Removed() };

            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, 2);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(1, outcome.RemovedCount); // удалённое сервером не теряется из счёта
        }

        [Fact]
        public void BatchDelete_UnfilledResult_IsNotSuccess()
        {
            // Аудит M4: незаполненный ответ адаптера (default) не должен выглядеть как «удалено».
            var results = new List<DeleteResult> { DeleteResult.Removed(), default(DeleteResult) };

            BatchDeleteOutcome outcome = CompletenessGuard.EvaluateBatchDelete(results, 2);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(1, outcome.RemovedCount);
            Assert.Contains("Unknown", outcome.Error);
        }
    }
}
