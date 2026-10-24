using System;
using System.Collections.Generic;

namespace IwoBridge.Core.Paging
{
    /// <summary>
    /// Единственное место проверки полноты ответов сервера (DRY). Чистая логика: на вход — уже
    /// извлечённые адаптером числа и исходы, на выходе — вердикт. Разбор XML EWS сюда не попадает (SoC).
    ///
    /// Два правила защиты от потери данных (PLAN.md §3, T30, T32):
    ///  - постраничный обход считается полным только если сервер пометил последнюю страницу и число
    ///    собранных уникальных элементов совпало с заявленным;
    ///  - пакетное удаление успешно только если учтены все запрошенные элементы (удалены либо уже
    ///    отсутствовали); частичный результат — ошибка, не маскируется.
    /// </summary>
    public static class CompletenessGuard
    {
        /// <summary>Вердикт о полноте постраничного перечисления.</summary>
        public static Completeness EvaluatePaging(
            int totalItemsInView, int distinctItemsCollected, bool includesLastItemInRange)
        {
            if (totalItemsInView < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalItemsInView));
            }

            if (distinctItemsCollected < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distinctItemsCollected));
            }

            if (!includesLastItemInRange)
            {
                return Completeness.Incomplete(
                    "Постраничный обход не завершён: сервер не пометил последнюю страницу (IncludesLastItemInRange=false).");
            }

            if (distinctItemsCollected != totalItemsInView)
            {
                return Completeness.Incomplete(
                    $"Расхождение числа элементов: собрано {distinctItemsCollected}, сервер заявил {totalItemsInView} — набор менялся во время обхода.");
            }

            return Completeness.Complete();
        }

        /// <summary>
        /// Строгая оценка пакетного удаления. Успех только если на каждый запрошенный элемент пришёл
        /// исход и все учтены (удалены или уже отсутствовали).
        /// </summary>
        public static BatchDeleteOutcome EvaluateBatchDelete(
            IReadOnlyList<DeleteResult> results, int requestedCount)
        {
            if (results is null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            if (requestedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedCount));
            }

            if (requestedCount == 0)
            {
                return BatchDeleteOutcome.Success(0);
            }

            int accounted = 0;
            int failed = 0;
            string? firstErrorCode = null;
            foreach (DeleteResult r in results)
            {
                if (r.IsAccountedFor)
                {
                    accounted++;
                }
                else
                {
                    failed++;
                    if (firstErrorCode is null)
                    {
                        firstErrorCode = r.ErrorCode ?? r.Status.ToString();
                    }
                }
            }

            // Число исходов не совпало с запросом — сопоставить их с элементами нельзя, пакет не выполнен. Учтённые
            // исходы всё равно сообщаются: сервер их уже удалил, и счёт удалённого не должен занижаться.
            if (results.Count != requestedCount)
            {
                return BatchDeleteOutcome.Failure(
                    $"Сервер вернул {results.Count} исходов на {requestedCount} запрошенных элементов.",
                    Math.Min(accounted, requestedCount));
            }

            if (failed > 0)
            {
                return BatchDeleteOutcome.Failure(
                    $"Частично удалено {accounted} из {requestedCount}, не удалено {failed} (например, {firstErrorCode}).",
                    accounted);
            }

            return BatchDeleteOutcome.Success(accounted);
        }
    }
}
