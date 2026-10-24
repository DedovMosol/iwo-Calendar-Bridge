using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Model;
using IwoBridge.Core.Reading;
using Xunit;
using static IwoBridge.Core.Tests.TestData;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Постраничное чтение снимка приёмника (T30, T32): якорный элемент, повтор обхода при сдвиге набора, урезанные сервером страницы, ограниченное число повторов и защита от зависания на пустых страницах.
    /// </summary>
    public sealed class CalendarSnapshotReaderTests
    {
        private static TargetItemInfo Item(string id) => Single(id, Utc(2026, 1, 15), Utc(2026, 1, 15, 1));

        private static List<TargetItemInfo> Items(int count) =>
            Enumerable.Range(0, count).Select(i => Item("id" + i.ToString("D3"))).ToList();

        private static string[] Ids(TargetSnapshot snapshot) => snapshot.Items.Select(i => i.ItemId).ToArray();

        [Fact]
        public async Task SinglePage_Complete_IsAuthoritative()
        {
            var reader = new ListPageReader(Items(3));

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 10).ReadAsync(reader, CancellationToken.None);

            Assert.True(snapshot.IsAuthoritative);
            Assert.Equal(3, snapshot.Items.Count);
            Assert.Equal(1, reader.Calls);
        }

        [Fact]
        public async Task MultiPage_RequestsAnchorAndReadsEveryItemOnce()
        {
            var reader = new ListPageReader(Items(10));

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 3, restartDelay: TimeSpan.Zero).ReadAsync(reader, CancellationToken.None);

            Assert.True(snapshot.IsAuthoritative);
            Assert.Equal(Items(10).Select(i => i.ItemId), Ids(snapshot));
            Assert.Equal(new[] { 0, 3, 6 }, reader.Offsets);
            Assert.All(reader.RequestedSizes, size => Assert.Equal(4, size)); // страница + якорь
        }

        [Fact]
        public async Task ServerReturnsFewerItemsThanRequested_StillReadsAll()
        {
            var reader = new ListPageReader(Items(10), serverCap: 2);

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 5, restartDelay: TimeSpan.Zero).ReadAsync(reader, CancellationToken.None);

            Assert.True(snapshot.IsAuthoritative);
            Assert.Equal(Items(10).Select(i => i.ItemId), Ids(snapshot));
        }

        [Fact]
        public async Task DeletionsBeforeCursorWhilePaging_RestartsAndDoesNotSkipItems()
        {
            // После первой страницы удаляются два элемента до курсора: без якоря «id004» был бы пропущен.
            bool deleted = false;
            var reader = new ListPageReader(Items(10), afterPage: list =>
            {
                if (!deleted)
                {
                    list.RemoveRange(0, 2);
                    deleted = true;
                }
            });

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 3, restartDelay: TimeSpan.Zero).ReadAsync(reader, CancellationToken.None);

            Assert.True(snapshot.IsAuthoritative);
            Assert.Equal(8, snapshot.Items.Count);
            Assert.Contains("id004", Ids(snapshot));
            Assert.DoesNotContain("id000", Ids(snapshot));
            Assert.DoesNotContain("id001", Ids(snapshot));
        }

        [Fact]
        public async Task StaleTotalCount_AnchorStillDetectsShift()
        {
            // Сервер сообщает устаревшее общее число (9), которое совпадёт с числом собранных элементов при
            // пропуске «id004»: проверка количества тут бессильна, сдвиг обнаруживает только якорь.
            bool deleted = false;
            var reader = new ListPageReader(Items(10), totalOverride: 9, afterPage: list =>
            {
                if (!deleted)
                {
                    list.RemoveRange(0, 2);
                    deleted = true;
                }
            });

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 3, restartDelay: TimeSpan.Zero).ReadAsync(reader, CancellationToken.None);

            Assert.False(snapshot.IsAuthoritative);
        }

        [Fact]
        public async Task PersistentChanges_IncompleteAfterBoundedRestarts()
        {
            var reader = new ListPageReader(Items(60), afterPage: list => list.RemoveAt(0));

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 3, maxRestarts: 2, restartDelay: TimeSpan.Zero)
                .ReadAsync(reader, CancellationToken.None);

            Assert.False(snapshot.IsAuthoritative);
            Assert.Equal(6, reader.Calls); // 3 прохода, в каждом сдвиг обнаружен на второй странице
        }

        [Fact]
        public async Task CountMismatchAtEnd_IncompleteAfterRestarts()
        {
            var reader = new ListPageReader(Items(4), totalBias: 1);

            TargetSnapshot snapshot = await new CalendarSnapshotReader(pageSize: 10, maxRestarts: 3, restartDelay: TimeSpan.Zero)
                .ReadAsync(reader, CancellationToken.None);

            Assert.False(snapshot.IsAuthoritative);
            Assert.Equal(4, reader.Calls); // исходный проход и три повтора
        }

        [Fact]
        public async Task EmptyPageWithMoreAvailable_IncompleteWithoutHang()
        {
            var reader = new FixedPageReader(new TargetPage(Array.Empty<TargetItemInfo>(), totalCount: 10, includesLastItemInRange: false));

            TargetSnapshot snapshot = await new CalendarSnapshotReader().ReadAsync(reader, CancellationToken.None);

            Assert.False(snapshot.IsAuthoritative);
            Assert.Equal(1, reader.Calls);
        }

        [Fact]
        public async Task SingleItemPageWithMoreAvailable_IncompleteWithoutHang()
        {
            var reader = new FixedPageReader(new TargetPage(new[] { Item("a") }, totalCount: 10, includesLastItemInRange: false));

            TargetSnapshot snapshot = await new CalendarSnapshotReader().ReadAsync(reader, CancellationToken.None);

            Assert.False(snapshot.IsAuthoritative);
            Assert.Equal(1, reader.Calls);
        }

        [Fact]
        public async Task CancelledToken_Throws()
        {
            var reader = new ListPageReader(Items(3));
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new CalendarSnapshotReader().ReadAsync(reader, cts.Token));
        }

        [Fact]
        public void Constructor_ValidatesArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CalendarSnapshotReader(pageSize: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CalendarSnapshotReader(pageSize: 1000));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CalendarSnapshotReader(maxRestarts: -1));
            Assert.NotNull(new CalendarSnapshotReader(pageSize: 999));
        }

        /// <summary>Читатель страниц поверх списка: учитывает смещение и размер, как сервер.</summary>
        private sealed class ListPageReader : ITargetPageReader
        {
            private readonly List<TargetItemInfo> _items;
            private readonly int _serverCap;
            private readonly int _totalBias;
            private readonly int? _totalOverride;
            private readonly Action<List<TargetItemInfo>>? _afterPage;

            public ListPageReader(
                IEnumerable<TargetItemInfo> items,
                int serverCap = int.MaxValue,
                int totalBias = 0,
                int? totalOverride = null,
                Action<List<TargetItemInfo>>? afterPage = null)
            {
                _items = items.ToList();
                _serverCap = serverCap;
                _totalBias = totalBias;
                _totalOverride = totalOverride;
                _afterPage = afterPage;
            }

            public List<int> Offsets { get; } = new List<int>();

            public List<int> RequestedSizes { get; } = new List<int>();

            public int Calls => Offsets.Count;

            public Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Offsets.Count >= 10_000)
                {
                    throw new InvalidOperationException("Обход не остановился.");
                }

                Offsets.Add(offset);
                RequestedSizes.Add(pageSize);
                List<TargetItemInfo> page = _items.Skip(offset).Take(Math.Min(pageSize, _serverCap)).ToList();
                bool last = offset + page.Count >= _items.Count;
                var result = new TargetPage(page, _totalOverride ?? (_items.Count + _totalBias), last);
                _afterPage?.Invoke(_items);
                return Task.FromResult(result);
            }
        }

        /// <summary>Читатель, всегда отдающий одну и ту же страницу.</summary>
        private sealed class FixedPageReader : ITargetPageReader
        {
            private readonly TargetPage _page;

            public FixedPageReader(TargetPage page)
            {
                _page = page;
            }

            public int Calls { get; private set; }

            public Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
            {
                Calls++;
                if (Calls > 10_000)
                {
                    throw new InvalidOperationException("Обход не остановился.");
                }

                return Task.FromResult(_page);
            }
        }
    }
}
