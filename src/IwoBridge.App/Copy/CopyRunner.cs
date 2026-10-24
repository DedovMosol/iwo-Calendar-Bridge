using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;

namespace IwoBridge.App.Copy
{
    /// <summary>Этап копирования — для хода работы в окне.</summary>
    public enum CopyStage
    {
        ReadingTarget,
        ReadingSource,
        Writing,
    }

    /// <summary>Сообщение о ходе копирования; <see cref="Total"/> = 0 — объём пока неизвестен.</summary>
    public sealed class CopyProgress
    {
        public CopyProgress(CopyStage stage, int completed = 0, int total = 0)
        {
            Stage = stage;
            Completed = completed;
            Total = total;
        }

        public CopyStage Stage { get; }

        public int Completed { get; }

        public int Total { get; }
    }

    /// <summary>Предпросмотр копирования: план за период и события источника, по которым он построен.</summary>
    public sealed class CopyPreview
    {
        internal CopyPreview(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            SyncPlan plan,
            IReadOnlyDictionary<string, SourceItem> sourceItems,
            IReadOnlyList<string> warnings,
            ISet<string> matchingOwn)
        {
            WindowStartUtc = windowStartUtc;
            WindowEndUtc = windowEndUtc;
            Plan = plan;
            SourceItems = sourceItems;
            Warnings = warnings;
            MatchingOwn = matchingOwn;
            Recommended = plan.Actions.Where(a => !IsDuplicateOfOwn(a)).ToArray();
        }

        /// <summary>
        /// События, для которых план создаёт копию, хотя в приёмнике уже есть такое же собственное событие человека
        /// (<see cref="OwnEventMatcher"/>). По умолчанию такие копии не создаются.
        /// </summary>
        public ISet<string> MatchingOwn { get; }

        /// <summary>Действия, выполняемые по умолчанию (и при автокопировании): весь план, кроме дублей своих событий.</summary>
        public IReadOnlyList<SyncAction> Recommended { get; }

        public bool IsDuplicateOfOwn(SyncAction action) =>
            action != null && action.Kind == SyncActionKind.Create && MatchingOwn.Contains(action.SourceId);

        /// <summary>Замечания чтения источника (<see cref="SourceRead.Warnings"/>) — показываются в предпросмотре.</summary>
        public IReadOnlyList<string> Warnings { get; }

        public DateTime WindowStartUtc { get; }

        public DateTime WindowEndUtc { get; }

        public SyncPlan Plan { get; }

        /// <summary>События источника по идентификатору (уже подготовленные правилами переноса).</summary>
        public IReadOnlyDictionary<string, SourceItem> SourceItems { get; }
    }

    /// <summary>Сбой одного действия: что не удалось и почему. Остальные действия выполняются дальше.</summary>
    public sealed class CopyFailure
    {
        internal CopyFailure(SyncAction action, Exception exception)
        {
            Action = action;
            Exception = exception;
        }

        public SyncAction Action { get; }

        public Exception Exception { get; }
    }

    /// <summary>Итог копирования: сколько создано, перезаписано, удалено; сбои по отдельным событиям.</summary>
    public sealed class CopyResult
    {
        internal CopyResult(int created, int updated, int deleted, IReadOnlyList<CopyFailure> failures, bool cancelled, string? refusedReason, Exception? fatal)
        {
            Created = created;
            Updated = updated;
            Deleted = deleted;
            Failures = failures;
            Cancelled = cancelled;
            RefusedReason = refusedReason;
            Fatal = fatal;
        }

        public int Created { get; }

        public int Updated { get; }

        public int Deleted { get; }

        public IReadOnlyList<CopyFailure> Failures { get; }

        /// <summary>Остановлено пользователем (после текущего события).</summary>
        public bool Cancelled { get; }

        /// <summary>План не применялся (подозрение на массовое удаление); <c>null</c> — применялся.</summary>
        public string? RefusedReason { get; }

        /// <summary>
        /// Ошибка, из-за которой копирование прекращено: нет связи, неверный вход — продолжать по остальным событиям
        /// бессмысленно, а повтор входа ещё и приблизил бы блокировку учётной записи. <c>null</c> — не было.
        /// </summary>
        public Exception? Fatal { get; }

        public bool Succeeded => RefusedReason is null && Fatal is null && !Cancelled && Failures.Count == 0;

        public int Done => Created + Updated + Deleted;
    }

    /// <summary>
    /// Сценарий копирования календаря из источника в приёмник за период — точечно, по разнице (PLAN.md §5):
    /// предпросмотр → человек выбирает, какие действия выполнить → выполнение с ходом работы и остановкой → итог.
    ///
    /// Порядок предпросмотра: сначала копии приёмника (на них ссылается снимок источника), затем источник за период
    /// и всё, на что ссылаются копии периода, затем план (<see cref="SyncPlanner"/>). Правила переноса
    /// (<see cref="CopyRules"/>) применяются один раз — к прочитанному.
    /// Выполнение: действия по порядку плана; сбой одного события не останавливает остальные (он в итоге), а
    /// «неустранимая» ошибка — нет связи, неверный вход (<c>isFatal</c>) — останавливает всё. Вложения читаются из
    /// источника только для того, что пишется. Класс без разделяемого изменяемого состояния.
    /// </summary>
    public sealed class CopyRunner
    {
        private readonly ISourceCalendar _source;
        private readonly ICopyTarget _target;
        private readonly Func<Exception, bool> _isFatal;

        private readonly PrivateEvents _privateEvents;

        /// <param name="isFatal">Ошибка, после которой продолжать нельзя (по умолчанию — никакая).</param>
        /// <param name="privateEvents">Что делать с личными событиями источника (<see cref="CopyRules"/>).</param>
        public CopyRunner(ISourceCalendar source, ICopyTarget target, Func<Exception, bool>? isFatal = null, PrivateEvents privateEvents = PrivateEvents.HideDetails)
        {
            _privateEvents = privateEvents;
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _isFatal = isFatal ?? (_ => false);
        }

        public string SourceLabel => _source.Label;

        /// <param name="allowMassDelete">Пользователь явно подтвердил массовое удаление копий.</param>
        public async Task<CopyPreview> PreviewAsync(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            bool allowMassDelete,
            IProgress<CopyProgress>? progress,
            CancellationToken cancellationToken)
        {
            DateTime start = TimeMapper.ToUtc(windowStartUtc);
            DateTime end = TimeMapper.ToUtc(windowEndUtc);
            if (end <= start)
            {
                throw new ArgumentException("Конец периода должен быть позже начала.", nameof(windowEndUtc));
            }

            progress?.Report(new CopyProgress(CopyStage.ReadingTarget));
            IReadOnlyList<BridgeCopy> copies = await _target.ReadCopiesAsync(cancellationToken).ConfigureAwait(false);
            string[] referenced = copies
                .Where(c => TimeWindow.Overlaps(c.SpanStartUtc, c.SpanEndUtc, start, end))
                .Select(c => c.SourceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            progress?.Report(new CopyProgress(CopyStage.ReadingSource));
            IProgress<int>? counter = progress is null ? null : new MappedProgress<int>(n => progress.Report(new CopyProgress(CopyStage.ReadingSource, n)));
            SourceRead read = await _source.ReadAsync(start, end, referenced, counter, cancellationToken).ConfigureAwait(false);

            // Повторяющийся идентификатор планировщик сам перечислит в конфликтах; в словарь идёт первое событие.
            SourceItem[] prepared = read.Items.Select(i => CopyRules.Prepare(i, _privateEvents)).Where(i => i != null).ToArray()!;
            var items = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
            foreach (SourceItem item in prepared)
            {
                if (!items.ContainsKey(item.SourceId))
                {
                    items.Add(item.SourceId, item);
                }
            }

            var snapshot = new SourceSnapshot(prepared.Select(i => i.ToEvent()), read.IsComplete, read.IncompleteReason);
            SyncPlan plan = SyncPlanner.Plan(start, end, snapshot, copies, new SyncOptions(allowMassDelete: allowMassDelete));

            // Собственные события приёмника читаются, только если есть что создавать: дубль возможен лишь у новой копии.
            SourceItem[] creates = plan.Actions.Where(a => a.Kind == SyncActionKind.Create).Select(a => items[a.SourceId]).ToArray();
            ISet<string> matchingOwn = creates.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : OwnEventMatcher.Duplicates(creates, await _target.ReadOwnEventsAsync(start, end, cancellationToken).ConfigureAwait(false));
            return new CopyPreview(start, end, plan, items, read.Warnings, matchingOwn);
        }

        /// <summary>
        /// Автокопирование: предпросмотр и выполнение рекомендованных действий (<see cref="CopyPreview.Recommended"/>) без
        /// участия человека. Подозрение на массовое удаление не выполняется (итог <see cref="CopyResult.RefusedReason"/>) —
        /// его разбирает человек в обычном предпросмотре; по неполному чтению источника копии не удаляются (планировщик).
        /// </summary>
        public async Task<(CopyPreview Preview, CopyResult Result)> SyncAsync(
            DateTime windowStartUtc, DateTime windowEndUtc, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
        {
            CopyPreview preview = await PreviewAsync(windowStartUtc, windowEndUtc, false, progress, cancellationToken).ConfigureAwait(false);
            CopyResult result = await RunAsync(preview, preview.Recommended, progress, cancellationToken).ConfigureAwait(false);
            return (preview, result);
        }

        /// <summary>
        /// Выполнить выбранные действия предпросмотра (по порядку плана). Действия не из этого плана игнорируются.
        /// Отмена — после текущего события: результат <see cref="CopyResult.Cancelled"/> с точными счётчиками.
        /// </summary>
        public async Task<CopyResult> RunAsync(
            CopyPreview preview,
            IReadOnlyCollection<SyncAction> selected,
            IProgress<CopyProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (preview is null)
            {
                throw new ArgumentNullException(nameof(preview));
            }

            if (selected is null)
            {
                throw new ArgumentNullException(nameof(selected));
            }

            if (!preview.Plan.CanApply)
            {
                return new CopyResult(0, 0, 0, Array.Empty<CopyFailure>(), false, preview.Plan.BlockReason, null);
            }

            var chosen = new HashSet<SyncAction>(selected);
            SyncAction[] actions = preview.Plan.Actions.Where(chosen.Contains).ToArray();
            int created = 0, updated = 0, deleted = 0;
            var failures = new List<CopyFailure>();

            for (int i = 0; i < actions.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new CopyResult(created, updated, deleted, failures, true, null, null);
                }

                progress?.Report(new CopyProgress(CopyStage.Writing, i, actions.Length));
                SyncAction action = actions[i];
                try
                {
                    // Начатое действие доводится до конца: в порты отмена не передаётся, чтобы не оставить копию
                    // наполовину записанной.
                    switch (action.Kind)
                    {
                        case SyncActionKind.Create:
                            await CreateAsync(preview, action).ConfigureAwait(false);
                            created++;
                            break;
                        case SyncActionKind.Update:
                            await UpdateAsync(preview, action).ConfigureAwait(false);
                            updated++;
                            break;
                        case SyncActionKind.Recreate:
                            await _target.DeleteAsync(action.Copy!.ItemId, CancellationToken.None).ConfigureAwait(false);
                            await CreateAsync(preview, action).ConfigureAwait(false);
                            updated++;
                            break;
                        case SyncActionKind.Delete:
                            await _target.DeleteAsync(action.Copy!.ItemId, CancellationToken.None).ConfigureAwait(false);
                            deleted++;
                            break;
                        default:
                            throw new InvalidOperationException("Неизвестное действие копирования: " + action.Kind);
                    }
                }
#pragma warning disable CA1031 // Сбой одного события не должен останавливать остальные; он попадает в итог.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (_isFatal(ex))
                    {
                        return new CopyResult(created, updated, deleted, failures, false, null, ex);
                    }

                    failures.Add(new CopyFailure(action, ex));
                }
            }

            progress?.Report(new CopyProgress(CopyStage.Writing, actions.Length, actions.Length));
            return new CopyResult(created, updated, deleted, failures, false, null, null);
        }

        private async Task CreateAsync(CopyPreview preview, SyncAction action)
        {
            SourceItem item = preview.SourceItems[action.SourceId];
            IReadOnlyList<AttachmentContent> attachments = await LoadAttachmentsAsync(item).ConfigureAwait(false);
            await _target.CreateAsync(item, attachments, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task UpdateAsync(CopyPreview preview, SyncAction action)
        {
            SourceItem item = preview.SourceItems[action.SourceId];
            IReadOnlyList<AttachmentContent> attachments = await LoadAttachmentsAsync(item).ConfigureAwait(false);
            await _target.UpdateAsync(action.Copy!.ItemId, item, attachments, CancellationToken.None).ConfigureAwait(false);
        }

        private Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item)
        {
            return item.Attachments.Count == 0
                ? Task.FromResult<IReadOnlyList<AttachmentContent>>(Array.Empty<AttachmentContent>())
                : _source.LoadAttachmentsAsync(item, CancellationToken.None);
        }
    }
}
