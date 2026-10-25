using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Постраничный обход папки (<see cref="EwsPaging.ReadAllAsync{T}"/>) на модели папки, которая меняется между
    /// запросами страниц так же, как на сервере: элементы упорядочены по времени создания, удаление сдвигает следующие
    /// на одну позицию, новый элемент встаёт в конец.
    /// </summary>
    public sealed class EwsPagingTests
    {
        [Fact]
        public async Task StableFolder_ReadsEverything_InOrder_NoDuplicates()
        {
            var folder = new Folder(10, pageSize: 3);

            (IReadOnlyList<string> items, bool complete) = await folder.ReadAsync();

            Assert.True(complete);
            Assert.Equal(folder.Ids, items);
        }

        [Fact]
        public async Task DeletionInReadPart_PlusNewItem_SameTotal_Detected_ReadAgain()
        {
            // Без перекрытия страниц: e3 сдвинулся на место прочитанного, новый элемент вернул TotalCount к прежнему —
            // число сошлось бы, а e3 был бы пропущен.
            var folder = new Folder(9, pageSize: 3);
            folder.BeforeFetch = (call, ids) =>
            {
                if (call == 2)
                {
                    ids.Remove("e1");
                    ids.Add("new");
                }
            };

            (IReadOnlyList<string> items, bool complete) = await folder.ReadAsync();

            Assert.True(complete);
            Assert.Equal(folder.Ids, items);
            Assert.Contains("e3", items);
            Assert.True(folder.Calls > 4, "перечень должен быть прочитан заново");
        }

        [Fact]
        public async Task DeletionInReadPart_Detected()
        {
            var folder = new Folder(9, pageSize: 3);
            folder.BeforeFetch = (call, ids) =>
            {
                if (call == 3)
                {
                    ids.Remove("e0");
                }
            };

            (IReadOnlyList<string> items, bool complete) = await folder.ReadAsync();

            Assert.True(complete);
            Assert.Equal(folder.Ids, items);
        }

        [Fact]
        public async Task ChangesInUnreadPart_NeedNoRestart()
        {
            var folder = new Folder(9, pageSize: 3);
            folder.BeforeFetch = (call, ids) =>
            {
                if (call == 2)
                {
                    ids.Remove("e7");
                    ids.Add("new");
                }
            };

            (IReadOnlyList<string> items, bool complete) = await folder.ReadAsync();

            Assert.True(complete);
            Assert.Equal(folder.Ids, items);
            Assert.Equal(4, folder.Calls); // 0–2, 2–4, 4–6, 6–8: одно чтение, страницы с перекрытием на один элемент
        }

        [Fact]
        public async Task ServerTruncatesPages_FollowsNextOffset()
        {
            var folder = new Folder(10, pageSize: 4) { ServerLimit = 2 };

            (IReadOnlyList<string> items, bool complete) = await folder.ReadAsync();

            Assert.True(complete);
            Assert.Equal(folder.Ids, items);
        }

        [Fact]
        public async Task FolderChangesDuringEveryAttempt_ReportedIncomplete()
        {
            var folder = new Folder(9, pageSize: 3);
            folder.BeforeFetch = (call, ids) =>
            {
                if (ids.Count > 0 && call % 2 == 0)
                {
                    ids.RemoveAt(0);
                }
            };

            (IReadOnlyList<string> _, bool complete) = await folder.ReadAsync();

            Assert.False(complete);
        }

        [Fact]
        public async Task EmptyFolder_Complete()
        {
            (IReadOnlyList<string> items, bool complete) = await new Folder(0, pageSize: 3).ReadAsync();

            Assert.True(complete);
            Assert.Empty(items);
        }

        /// <summary>Папка сервера: страница по смещению из текущего состояния, перед каждым запросом — возможная правка.</summary>
        private sealed class Folder
        {
            private readonly List<string> _ids;
            private readonly int _pageSize;

            public Folder(int count, int pageSize)
            {
                _ids = Enumerable.Range(0, count).Select(i => "e" + i).ToList();
                _pageSize = pageSize;
            }

            public Action<int, List<string>>? BeforeFetch { get; set; }

            /// <summary>Сколько элементов сервер отдаёт за раз (EWSFindCountLimit), меньше запрошенного размера страницы.</summary>
            public int ServerLimit { get; set; } = int.MaxValue;

            public int Calls { get; private set; }

            public IReadOnlyList<string> Ids => _ids.ToArray();

            public Task<(IReadOnlyList<string> Items, bool Complete)> ReadAsync() =>
                EwsPaging.ReadAllAsync(Fetch, id => id, CancellationToken.None);

            private Task<EwsPaging.Page<string>> Fetch(int offset)
            {
                Calls++;
                BeforeFetch?.Invoke(Calls, _ids);
                string[] page = _ids.Skip(offset).Take(Math.Min(_pageSize, ServerLimit)).ToArray();
                int next = offset + page.Length;
                return Task.FromResult(new EwsPaging.Page<string>(page, _ids.Count, next < _ids.Count ? next : (int?)null, next < _ids.Count));
            }
        }
    }
}
