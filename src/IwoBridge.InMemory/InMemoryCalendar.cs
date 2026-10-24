using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using IwoBridge.Core.Reading;

namespace IwoBridge.InMemory
{
    /// <summary>
    /// Ин-мемори реализация портов приёмника — для прогонов и тестов без сервера. Повторяет поведение EWS,
    /// важное для логики:
    ///  - перечисление (FindItem) не раскрывает серии и не отдаёт границы, UID и исключения — их дочитывает
    ///    <see cref="LoadAsync"/> (GetItem);
    ///  - раскрытие окна (CalendarView) возвращает надмножество: элемент попадает, если его конец не раньше
    ///    начала окна, а начало раньше конца окна — то есть и событие, заканчивающееся ровно в начале окна;
    ///    окно с концом не позже начала отклоняется, как на сервере;
    ///  - удаление идентификатора мастера удаляет серию целиком, идентификатора экземпляра — только экземпляр;
    ///    неизвестный идентификатор даёт «уже отсутствует»;
    ///  - идентификаторы уникальны во всём календаре (и элементы, и экземпляры), как ItemId в EWS.
    ///
    /// Порядок перечисления — порядок добавления (как сортировка по дате создания). Поиск по идентификатору —
    /// через словари, поэтому и 100 000 элементов засеиваются и удаляются за линейное время. Все обращения под
    /// блокировкой, поэтому адаптер безопасен и при параллельном доступе.
    /// </summary>
    public sealed class InMemoryCalendar
        : ITargetPageReader, ITargetRecurrenceReader, ITargetOccurrenceReader, ITargetDeleter
    {
        private readonly object _sync = new object();

        // Элементы верхнего уровня в порядке добавления; удалённые помечаются (null) и вычищаются при чтении страницы,
        // чтобы удаление было O(1), а порядок — стабильным.
        private readonly List<Node?> _items = new List<Node?>();
        private readonly Dictionary<string, int> _indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, Node> _ownerByOccurrenceId = new Dictionary<string, Node>(StringComparer.Ordinal);
        private int _removedCount;

        public InMemoryCalendar(IEnumerable<InMemoryEvent> seed)
        {
            if (seed is null)
            {
                throw new ArgumentNullException(nameof(seed));
            }

            foreach (InMemoryEvent item in seed)
            {
                AddCore(item);
            }
        }

        /// <summary>Добавляет событие в конец — имитация события, созданного во время работы.</summary>
        public void Add(InMemoryEvent item)
        {
            lock (_sync)
            {
                AddCore(item);
            }
        }

        /// <summary>Идентификаторы элементов верхнего уровня, оставшихся в календаре (для проверок).</summary>
        public IReadOnlyList<string> ItemIds()
        {
            lock (_sync)
            {
                return Live().Select(n => n.ItemId).ToArray();
            }
        }

        /// <summary>Идентификаторы оставшихся экземпляров указанного элемента (для проверок).</summary>
        public IReadOnlyList<string> OccurrenceIds(string itemId)
        {
            lock (_sync)
            {
                Node? node = Find(itemId);
                return node == null
                    ? Array.Empty<string>()
                    : node.Occurrences.Select(o => o.OccurrenceItemId).ToArray();
            }
        }

        public Task<TargetPage> ReadPageAsync(int offset, int pageSize, CancellationToken cancellationToken)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (pageSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            cancellationToken.ThrowIfCancellationRequested();

            List<TargetItemInfo> page;
            int total;
            lock (_sync)
            {
                Compact();
                total = _items.Count;
                int count = Math.Max(0, Math.Min(pageSize, total - offset));
                page = new List<TargetItemInfo>(count);
                for (int i = offset; i < offset + count; i++)
                {
                    Node n = _items[i]!;
                    page.Add(new TargetItemInfo(
                        n.ItemId,
                        "ck-" + n.ItemId,
                        n.ItemClass,
                        n.IsMaster ? ItemKind.RecurringMaster : ItemKind.Single,
                        n.StartUtc,
                        n.EndUtc,
                        null,
                        null,
                        n.BridgeSourceId,
                        subject: n.Subject));
                }
            }

            bool includesLast = offset + page.Count >= total;
            return Task.FromResult(new TargetPage(page, total, includesLast));
        }

        public Task<IReadOnlyDictionary<string, RecurrenceInfo>> LoadAsync(
            IReadOnlyList<string> masterItemIds, CancellationToken cancellationToken)
        {
            if (masterItemIds is null)
            {
                throw new ArgumentNullException(nameof(masterItemIds));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var result = new Dictionary<string, RecurrenceInfo>(StringComparer.Ordinal);
            lock (_sync)
            {
                foreach (string id in masterItemIds)
                {
                    Node? node = Find(id);
                    RecurrenceInfo? info = node != null && node.IsMaster ? node.ToRecurrenceInfo() : null;
                    if (info != null)
                    {
                        result[id] = info;
                    }
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, RecurrenceInfo>>(result);
        }

        public Task<OccurrenceReadResult> ReadOccurrencesAsync(
            DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
        {
            if (windowEndUtc <= windowStartUtc)
            {
                throw new ArgumentException("Конец окна должен быть позже начала.", nameof(windowEndUtc));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var occurrences = new List<TargetOccurrence>();
            lock (_sync)
            {
                foreach (Node node in Live())
                {
                    foreach (InMemoryOccurrence occurrence in node.Occurrences)
                    {
                        // Семантика CalendarView: исключаются только элементы, закончившиеся раньше начала окна
                        // или начавшиеся не раньше его конца.
                        if (occurrence.EndUtc >= windowStartUtc && occurrence.StartUtc < windowEndUtc)
                        {
                            occurrences.Add(new TargetOccurrence(
                                occurrence.OccurrenceItemId,
                                node.Uid,
                                occurrence.StartUtc,
                                occurrence.EndUtc,
                                isSeriesInstance: node.IsMaster,
                                subject: occurrence.Subject ?? node.Subject));
                        }
                    }
                }
            }

            return Task.FromResult(new OccurrenceReadResult(occurrences, isComplete: true));
        }

        public Task<IReadOnlyList<DeleteResult>> DeleteAsync(
            IReadOnlyList<string> itemIds, TargetDeleteMode mode, CancellationToken cancellationToken)
        {
            if (itemIds is null)
            {
                throw new ArgumentNullException(nameof(itemIds));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var results = new List<DeleteResult>(itemIds.Count);
            lock (_sync)
            {
                foreach (string id in itemIds)
                {
                    if (_indexById.TryGetValue(id, out int index))
                    {
                        Node node = _items[index]!;
                        foreach (InMemoryOccurrence occurrence in node.Occurrences)
                        {
                            _ownerByOccurrenceId.Remove(occurrence.OccurrenceItemId);
                        }

                        _items[index] = null;
                        _indexById.Remove(id);
                        _removedCount++;
                        results.Add(DeleteResult.Removed());
                        continue;
                    }

                    if (_ownerByOccurrenceId.TryGetValue(id, out Node? owner) && owner.IsMaster)
                    {
                        owner.Occurrences.RemoveAll(o => o.OccurrenceItemId == id);
                        _ownerByOccurrenceId.Remove(id);
                        results.Add(DeleteResult.Removed());
                        continue;
                    }

                    results.Add(DeleteResult.AlreadyAbsent());
                }
            }

            return Task.FromResult<IReadOnlyList<DeleteResult>>(results);
        }

        private void AddCore(InMemoryEvent item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (IsKnownId(item.ItemId))
            {
                throw new ArgumentException($"Идентификатор «{item.ItemId}» уже есть в календаре.", nameof(item));
            }

            // Экземпляр одиночного события носит идентификатор самого события — это одно и то же.
            IEnumerable<InMemoryOccurrence> ownOccurrences = item.IsMaster ? item.Occurrences : Array.Empty<InMemoryOccurrence>();
            foreach (InMemoryOccurrence occurrence in ownOccurrences)
            {
                if (IsKnownId(occurrence.OccurrenceItemId) || occurrence.OccurrenceItemId == item.ItemId)
                {
                    throw new ArgumentException(
                        $"Идентификатор экземпляра «{occurrence.OccurrenceItemId}» уже есть в календаре.", nameof(item));
                }
            }

            var node = new Node(item);
            _indexById.Add(node.ItemId, _items.Count);
            _items.Add(node);
            foreach (InMemoryOccurrence occurrence in ownOccurrences)
            {
                _ownerByOccurrenceId.Add(occurrence.OccurrenceItemId, node);
            }
        }

        private bool IsKnownId(string id)
        {
            return _indexById.ContainsKey(id) || _ownerByOccurrenceId.ContainsKey(id);
        }

        private Node? Find(string itemId)
        {
            return _indexById.TryGetValue(itemId, out int index) ? _items[index] : null;
        }

        private IEnumerable<Node> Live()
        {
            foreach (Node? node in _items)
            {
                if (node != null)
                {
                    yield return node;
                }
            }
        }

        /// <summary>Вычищает удалённые элементы перед постраничным чтением, сохраняя порядок; индексы пересчитываются.</summary>
        private void Compact()
        {
            if (_removedCount == 0)
            {
                return;
            }

            _items.RemoveAll(n => n is null);
            _removedCount = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                _indexById[_items[i]!.ItemId] = i;
            }
        }

        private sealed class Node
        {
            public Node(InMemoryEvent source)
            {
                ItemId = source.ItemId;
                ItemClass = source.ItemClass;
                IsMaster = source.IsMaster;
                Uid = source.Uid;
                BridgeSourceId = source.BridgeSourceId;
                Subject = source.Subject;
                Occurrences = source.Occurrences.ToList();

                // Как у FindItem: начало и конец мастера — первого повторения по правилу. Фиксируются при
                // создании и не меняются при удалении отдельных экземпляров.
                InMemoryOccurrence anchor = RuleOccurrences().OrderBy(o => o.StartUtc).First();
                StartUtc = anchor.StartUtc;
                EndUtc = anchor.EndUtc;
            }

            public string ItemId { get; }

            public string ItemClass { get; }

            public bool IsMaster { get; }

            public string? Uid { get; }

            public string? BridgeSourceId { get; }

            public string? Subject { get; }

            public DateTime StartUtc { get; }

            public DateTime EndUtc { get; }

            public List<InMemoryOccurrence> Occurrences { get; }

            /// <summary>
            /// Данные для GetItem: границы по правилу (неизменённые повторения), UID и изменённые повторения.
            /// <c>null</c>, если у серии не осталось экземпляров — как недочитанные границы у сервера.
            /// </summary>
            public RecurrenceInfo? ToRecurrenceInfo()
            {
                List<InMemoryOccurrence> rule = RuleOccurrences().ToList();
                if (rule.Count == 0)
                {
                    return null;
                }

                List<OccurrenceSpan> modified = Occurrences
                    .Where(o => o.IsModified)
                    .Select(o => new OccurrenceSpan(o.StartUtc, o.EndUtc))
                    .ToList();

                return new RecurrenceInfo(rule.Min(o => o.StartUtc), rule.Max(o => o.StartUtc), Uid, modified);
            }

            private IEnumerable<InMemoryOccurrence> RuleOccurrences()
            {
                List<InMemoryOccurrence> unmodified = Occurrences.Where(o => !o.IsModified).ToList();
                return unmodified.Count > 0 ? unmodified : Occurrences;
            }
        }
    }
}
