using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;

namespace IwoBridge.InMemory
{
    /// <summary>
    /// Исходный календарь в памяти — для тестов и демонстрационного режима копирования. Ведёт себя как настоящий
    /// источник: отдаёт события, пересекающие период, плюс запрошенные по идентификатору; вложения — по запросу.
    /// </summary>
    public sealed class InMemorySource : ISourceCalendar
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, SourceItem> _items = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<AttachmentContent>> _attachments =
            new Dictionary<string, IReadOnlyList<AttachmentContent>>(StringComparer.Ordinal);

        public InMemorySource(string label, IEnumerable<SourceItem> items)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            foreach (SourceItem item in items ?? throw new ArgumentNullException(nameof(items)))
            {
                _items[item.SourceId] = item;
            }
        }

        public string Label { get; }

        /// <summary>Чтение будет неполным с этой причиной (как обрыв связи); <c>null</c> — полным.</summary>
        public string? IncompleteReason { get; set; }

        /// <summary>Сколько раз читались вложения (проверка: только для того, что пишется).</summary>
        public int AttachmentLoads { get; private set; }

        /// <summary>Добавить или заменить событие — имитация правки в источнике.</summary>
        public void Put(SourceItem item, IReadOnlyList<AttachmentContent>? attachments = null)
        {
            lock (_sync)
            {
                _items[item.SourceId] = item;
                if (attachments != null)
                {
                    _attachments[item.SourceId] = attachments;
                }
            }
        }

        /// <summary>Удалить событие — имитация удаления в источнике.</summary>
        public void Remove(string sourceId)
        {
            lock (_sync)
            {
                _items.Remove(sourceId);
            }
        }

        public Task<SourceRead> ReadAsync(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            IReadOnlyCollection<string> referencedSourceIds,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var referenced = new HashSet<string>(referencedSourceIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            SourceItem[] found;
            lock (_sync)
            {
                found = _items.Values
                    .Where(i => referenced.Contains(i.SourceId) || TimeWindow.Overlaps(i.StartUtc, i.SpanEndUtc, windowStartUtc, windowEndUtc))
                    .ToArray();
            }

            progress?.Report(found.Length);
            return Task.FromResult(new SourceRead(found, IncompleteReason));
        }

        public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                AttachmentLoads++;
                return Task.FromResult(_attachments.TryGetValue(item.SourceId, out IReadOnlyList<AttachmentContent>? list)
                    ? list
                    : (IReadOnlyList<AttachmentContent>)Array.Empty<AttachmentContent>());
            }
        }
    }

    /// <summary>Записанная в памяти копия: событие источника, по которому она записана, и вложения.</summary>
    public sealed class InMemoryCopy
    {
        internal InMemoryCopy(string itemId, SourceItem item, IReadOnlyList<AttachmentContent> attachments)
        {
            ItemId = itemId;
            Item = item;
            Attachments = attachments;
        }

        public string ItemId { get; }

        public SourceItem Item { get; }

        public IReadOnlyList<AttachmentContent> Attachments { get; }
    }

    /// <summary>
    /// Календарь-приёмник в памяти со стороны копирования. Копия хранит метку источника, отпечаток и охват — как
    /// расширенные свойства копии в Exchange. Сбой записи можно задать (<see cref="FailOn"/>) — для проверки итога.
    /// </summary>
    public sealed class InMemoryCopyTarget : ICopyTarget
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, InMemoryCopy> _copies = new Dictionary<string, InMemoryCopy>(StringComparer.Ordinal);
        private int _nextId;

        /// <summary>Исключение при записи этого события; <c>null</c> — запись проходит.</summary>
        public Func<SourceItem, Exception?>? FailOn { get; set; }

        /// <summary>Копии в календаре (для проверок и демонстрационного режима).</summary>
        public IReadOnlyList<InMemoryCopy> Copies
        {
            get
            {
                lock (_sync)
                {
                    return _copies.Values.OrderBy(c => c.ItemId, StringComparer.Ordinal).ToArray();
                }
            }
        }

        /// <summary>Положить копию напрямую — например, оставшуюся от прошлого копирования или дубль.</summary>
        public string Seed(SourceItem item)
        {
            lock (_sync)
            {
                return AddCore(item, Array.Empty<AttachmentContent>());
            }
        }

        public Task<IReadOnlyList<BridgeCopy>> ReadCopiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                IReadOnlyList<BridgeCopy> list = _copies.Values
                    .Select(c => new BridgeCopy(c.ItemId, c.Item.SourceId, c.Item.Kind, c.Item.StartUtc, c.Item.SpanEndUtc, c.Item.ContentHash, c.Item.Subject))
                    .ToArray();
                return Task.FromResult(list);
            }
        }

        /// <summary>Собственные события приёмника (созданные человеком) — их программа не трогает.</summary>
        public List<OwnEvent> Own { get; } = new List<OwnEvent>();

        public Task<IReadOnlyList<OwnEvent>> ReadOwnEventsAsync(DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                IReadOnlyList<OwnEvent> list = Own
                    .Where(o => o.Kind == Core.Model.ItemKind.RecurringMaster || TimeWindow.Overlaps(o.StartUtc, o.EndUtc, windowStartUtc, windowEndUtc))
                    .ToArray();
                return Task.FromResult(list);
            }
        }

        public Task CreateAsync(SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            ThrowIfFails(item);
            lock (_sync)
            {
                AddCore(item, attachments);
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(string copyItemId, SourceItem item, IReadOnlyList<AttachmentContent> attachments, CancellationToken cancellationToken)
        {
            ThrowIfFails(item);
            lock (_sync)
            {
                if (!_copies.ContainsKey(copyItemId))
                {
                    throw new InvalidOperationException("Копии " + copyItemId + " нет.");
                }

                _copies[copyItemId] = new InMemoryCopy(copyItemId, item, attachments);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string copyItemId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _copies.Remove(copyItemId);
            }

            return Task.CompletedTask;
        }

        private string AddCore(SourceItem item, IReadOnlyList<AttachmentContent> attachments)
        {
            string id = "copy-" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _copies[id] = new InMemoryCopy(id, item, attachments);
            return id;
        }

        private void ThrowIfFails(SourceItem item)
        {
            Exception? failure = FailOn?.Invoke(item);
            if (failure != null)
            {
                throw failure;
            }
        }
    }
}
