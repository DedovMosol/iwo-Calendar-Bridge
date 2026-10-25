using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;

namespace IwoBridge.Ews
{
    /// <summary>
    /// Полный перечень элементов папки постранично, с проверкой полноты (одно место для источника и приёмника, DRY).
    ///  - Следующая страница начинается с <see cref="FindItemsResults{T}.NextPageOffset"/>: Exchange 2010+ может урезать
    ///    страницу политикой EWSFindCountLimit, и шаг на полный размер страницы пропустил бы остаток.
    ///  - Страницы перекрываются на один элемент: следующая запрашивается с последнего прочитанного, и он должен прийти
    ///    первым. Удаление или вставка в уже прочитанной части сдвигает перечень, и один элемент оказался бы пропущен,
    ///    а сверка числа с <c>TotalCount</c> такого не видит, если папку одновременно пополнили. Сдвиг — перечень
    ///    читается заново, не больше <see cref="Attempts"/> раз.
    ///  - Итог сверяется с числом элементов, которое называет сервер на последней странице.
    /// </summary>
    internal static class EwsPaging
    {
        private const int PageSize = 500;
        private const int Attempts = 3;

        /// <returns>Элементы и признак полноты: <c>false</c> — папка менялась во время всех попыток.</returns>
        public static Task<(IReadOnlyList<Item> Items, bool Complete)> FindAllAsync(
            EwsChannel channel, FolderId folder, SearchFilter? filter, PropertySet properties, CancellationToken cancellationToken)
        {
            return ReadAllAsync(
                async offset =>
                {
                    var view = new ItemView(PageSize, offset, OffsetBasePoint.Beginning)
                    {
                        Traversal = ItemTraversal.Shallow,
                        PropertySet = properties,
                    };
                    view.OrderBy.Add(ItemSchema.DateTimeCreated, SortDirection.Ascending);
                    FindItemsResults<Item> page = await channel
                        .RunAsync(service => filter is null ? service.FindItems(folder, view) : service.FindItems(folder, filter, view), cancellationToken)
                        .ConfigureAwait(false);
                    return new Page<Item>(page.Items, page.TotalCount, page.NextPageOffset, page.MoreAvailable);
                },
                item => item.Id.UniqueId,
                cancellationToken);
        }

        /// <summary>Алгоритм обхода без типов EWS: страница по смещению → полный перечень и признак полноты.</summary>
        internal static async Task<(IReadOnlyList<T> Items, bool Complete)> ReadAllAsync<T>(
            Func<int, Task<Page<T>>> fetch, Func<T, string> idOf, CancellationToken cancellationToken)
        {
            IReadOnlyList<T> last = Array.Empty<T>();
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var all = new List<T>();
                bool shifted = false;
                int total = 0;
                for (int expected = 0; ;)
                {
                    // Со второй страницы — с последнего прочитанного элемента (перекрытие на один).
                    int from = all.Count == 0 ? expected : expected - 1;
                    Page<T> page = await fetch(from).ConfigureAwait(false);
                    total = page.TotalCount;
                    int first = 0;
                    if (all.Count > 0)
                    {
                        if (page.Items.Count == 0 || !string.Equals(idOf(page.Items[0]), idOf(all[all.Count - 1]), StringComparison.Ordinal))
                        {
                            shifted = true;
                            break;
                        }

                        first = 1;
                    }

                    for (int i = first; i < page.Items.Count; i++)
                    {
                        all.Add(page.Items[i]);
                    }

                    int next = page.NextOffset ?? from + page.Items.Count;
                    if (!page.MoreAvailable || next <= expected)
                    {
                        break;
                    }

                    expected = next;
                }

                if (!shifted && all.Count == total)
                {
                    return (all, true);
                }

                last = all;
            }

            return (last, false);
        }

        /// <summary>Страница перечня: элементы, общее число по словам сервера, смещение следующей страницы, есть ли ещё.</summary>
        internal sealed class Page<T>
        {
            public Page(IReadOnlyList<T> items, int totalCount, int? nextOffset, bool moreAvailable)
            {
                Items = items;
                TotalCount = totalCount;
                NextOffset = nextOffset;
                MoreAvailable = moreAvailable;
            }

            public IReadOnlyList<T> Items { get; }

            public int TotalCount { get; }

            public int? NextOffset { get; }

            public bool MoreAvailable { get; }
        }
    }
}
