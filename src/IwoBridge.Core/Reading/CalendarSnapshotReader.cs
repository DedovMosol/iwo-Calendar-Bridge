using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;

namespace IwoBridge.Core.Reading
{
    /// <summary>
    /// Постраничное чтение снимка приёмника с обнаружением изменений набора во время обхода (T30).
    ///
    /// Используется приём «якорного элемента» из документации Microsoft («Perform paged searches by using
    /// EWS in Exchange»): каждая страница запрашивается на один элемент больше; последний элемент страницы
    /// служит якорем, и следующая страница обязана начинаться с него. Несовпадение означает, что набор
    /// сдвинулся (удаление или вставка раньше текущей позиции) и часть элементов могла быть пропущена.
    /// Тогда обход повторяется с начала (ограниченное число раз), иначе снимок помечается неполным и
    /// разрушительные операции запрещаются. Так же обрабатывается расхождение числа уникальных элементов
    /// с заявленным сервером в конце обхода.
    ///
    /// Смещение сдвигается на фактическое число элементов страницы минус якорь, поэтому обход корректен и
    /// при серверном ограничении размера ответа. Защита от зависания: страница без признака конца, в которой
    /// меньше двух элементов, и жёсткий потолок числа страниц дают неполный снимок.
    ///
    /// Предел приёма (честно): если между двумя страницами раньше текущей позиции одновременно удалили и вставили
    /// по элементу, сдвига нет, и якорь его не видит. Последствия безопасны: удалённое попадёт в план и на удалении
    /// вернётся «уже отсутствует», вставленное не попадёт — его не тронут. Риск дополнительно снижает сортировка по
    /// дате создания (новые элементы идут в конец) и свежая сверка перед удалением.
    /// </summary>
    public sealed class CalendarSnapshotReader
    {
        private const int MaxPagesHardCap = 100_000;
        private const int MinPageSize = 1;

        // Страница + якорь укладываются в ограничение Exchange по умолчанию — 1000 элементов за запрос.
        private const int MaxPageSize = 999;

        private readonly int _pageSize;
        private readonly int _maxRestarts;
        private readonly TimeSpan _restartDelay;

        /// <param name="pageSize">Элементов на страницу (плюс якорь).</param>
        /// <param name="maxRestarts">Сколько раз перечитать календарь, если он менялся во время чтения.</param>
        /// <param name="restartDelay">
        /// Пауза перед перечитыванием: календарь, который как раз меняется (например, телефон синхронизирует удаление),
        /// успевает успокоиться, а сервер не получает несколько полных обходов подряд. По умолчанию — 2 с, в тестах — 0.
        /// </param>
        public CalendarSnapshotReader(int pageSize = 500, int maxRestarts = 3, TimeSpan? restartDelay = null)
        {
            if (pageSize < MinPageSize || pageSize > MaxPageSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageSize), pageSize, $"Размер страницы должен быть в пределах {MinPageSize}..{MaxPageSize}.");
            }

            if (maxRestarts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRestarts), maxRestarts, "Число повторов не может быть отрицательным.");
            }

            TimeSpan delay = restartDelay ?? TimeSpan.FromSeconds(2);
            if (delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(restartDelay), delay, "Пауза не может быть отрицательной.");
            }

            _pageSize = pageSize;
            _maxRestarts = maxRestarts;
            _restartDelay = delay;
        }

        public Task<TargetSnapshot> ReadAsync(ITargetPageReader reader, CancellationToken cancellationToken)
        {
            return ReadAsync(reader, null, cancellationToken);
        }

        /// <param name="reader">Порт постраничного чтения.</param>
        /// <param name="progress">Ход чтения после каждой страницы; <c>null</c> — не сообщать.</param>
        /// <param name="cancellationToken">Отмена — между страницами.</param>
        public async Task<TargetSnapshot> ReadAsync(
            ITargetPageReader reader, IProgress<SnapshotReadProgress>? progress, CancellationToken cancellationToken)
        {
            if (reader is null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            for (int attempt = 0; ; attempt++)
            {
                Pass pass = await ReadPassAsync(reader, progress, cancellationToken).ConfigureAwait(false);
                if (!pass.SetChanged)
                {
                    return new TargetSnapshot(pass.Items, pass.Completeness);
                }

                if (attempt >= _maxRestarts)
                {
                    return new TargetSnapshot(
                        pass.Items,
                        Completeness.Incomplete(
                            $"Календарь менялся во время чтения (попыток: {attempt + 1}) — снимок неполный."));
                }

                if (_restartDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_restartDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<Pass> ReadPassAsync(
            ITargetPageReader reader, IProgress<SnapshotReadProgress>? progress, CancellationToken cancellationToken)
        {
            var items = new List<TargetItemInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int requestSize = _pageSize + 1;
            int offset = 0;
            int pages = 0;
            int lastTotalCount = 0;
            bool reachedLastPage = false;
            string? anchorId = null;

            while (pages < MaxPagesHardCap)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TargetPage page = await reader.ReadPageAsync(offset, requestSize, cancellationToken).ConfigureAwait(false);
                pages++;
                lastTotalCount = page.TotalCount;

                if (anchorId != null
                    && (page.Items.Count == 0 || !string.Equals(page.Items[0].ItemId, anchorId, StringComparison.Ordinal)))
                {
                    return Pass.Changed(items);
                }

                foreach (TargetItemInfo item in page.Items)
                {
                    if (seen.Add(item.ItemId))
                    {
                        items.Add(item);
                    }
                }

                progress?.Report(new SnapshotReadProgress(items.Count, page.TotalCount));

                if (page.IncludesLastItemInRange)
                {
                    reachedLastPage = true;
                    break;
                }

                if (page.Items.Count < 2)
                {
                    // Сервер заявил «есть ещё», но страница не даёт продвинуться с якорем — нет прогресса.
                    break;
                }

                anchorId = page.Items[page.Items.Count - 1].ItemId;
                offset += page.Items.Count - 1;
            }

            if (reachedLastPage && items.Count != lastTotalCount)
            {
                return Pass.Changed(items);
            }

            return Pass.Finished(items, CompletenessGuard.EvaluatePaging(lastTotalCount, items.Count, reachedLastPage));
        }

        private sealed class Pass
        {
            private Pass(IReadOnlyList<TargetItemInfo> items, Completeness completeness, bool setChanged)
            {
                Items = items;
                Completeness = completeness;
                SetChanged = setChanged;
            }

            public IReadOnlyList<TargetItemInfo> Items { get; }

            public Completeness Completeness { get; }

            public bool SetChanged { get; }

            public static Pass Changed(IEnumerable<TargetItemInfo> items)
            {
                return new Pass(items.ToArray(), Completeness.Incomplete("Набор менялся во время обхода."), true);
            }

            public static Pass Finished(IEnumerable<TargetItemInfo> items, Completeness completeness)
            {
                return new Pass(items.ToArray(), completeness, false);
            }
        }
    }
}
