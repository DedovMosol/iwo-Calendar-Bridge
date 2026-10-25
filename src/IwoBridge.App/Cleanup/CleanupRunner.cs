using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Abstractions;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using IwoBridge.Core.Reading;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Сценарий очистки приёмника — композиция чистых частей ядра поверх портов (DIP).
    ///
    /// Предпросмотр: снимок (постранично, с якорем) → в режиме «период» дочитывание границ серий и раскрытие
    /// окна → план. Выполнение принимает подтверждённый предпросмотр и действует по принципу «сначала все
    /// чтения, потом записи»: строится свежий план, и удаляется только пересечение подтверждённого и
    /// свежего. Появившееся после предпросмотра не трогается, исчезнувшее или вышедшее из периода —
    /// тоже; оба случая видны в отчёте. Перед первым удалением записывается манифест — ровно список
    /// удаления; не удалось записать — удаления нет. Любые неполные данные дают безопасный отказ без
    /// удаления. Класс без разделяемого изменяемого состояния.
    /// </summary>
    public sealed class CleanupRunner
    {
        private readonly ITargetPageReader _pageReader;
        private readonly ITargetRecurrenceReader _recurrenceReader;
        private readonly ITargetOccurrenceReader _occurrenceReader;
        private readonly ITargetDeleter _deleter;
        private readonly ICleanupManifestWriter _manifestWriter;
        private readonly CalendarSnapshotReader _snapshotReader;
        private readonly CleanupExecutor _executor;

        public CleanupRunner(
            ITargetPageReader pageReader,
            ITargetRecurrenceReader recurrenceReader,
            ITargetOccurrenceReader occurrenceReader,
            ITargetDeleter deleter,
            ICleanupManifestWriter manifestWriter,
            CalendarSnapshotReader? snapshotReader = null,
            CleanupExecutor? executor = null)
        {
            _pageReader = pageReader ?? throw new ArgumentNullException(nameof(pageReader));
            _recurrenceReader = recurrenceReader ?? throw new ArgumentNullException(nameof(recurrenceReader));
            _occurrenceReader = occurrenceReader ?? throw new ArgumentNullException(nameof(occurrenceReader));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
            _manifestWriter = manifestWriter ?? throw new ArgumentNullException(nameof(manifestWriter));
            _snapshotReader = snapshotReader ?? new CalendarSnapshotReader();
            _executor = executor ?? new CleanupExecutor();
        }

        /// <summary>Пробный прогон: строит план без удаления.</summary>
        public Task<CleanupPreview> PreviewAsync(CleanupScope scope, CleanupFilter filter, CancellationToken cancellationToken)
        {
            return PreviewAsync(scope, filter, null, cancellationToken);
        }

        /// <summary>Пробный прогон с сообщениями о ходе чтения (<paramref name="progress"/> может быть <c>null</c>).</summary>
        public Task<CleanupPreview> PreviewAsync(
            CleanupScope scope, CleanupFilter filter, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            if (scope is null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            return BuildAsync(scope, filter, progress, cancellationToken);
        }

        /// <summary>Выполнение подтверждённого предпросмотра.</summary>
        public Task<CleanupResult> RunAsync(CleanupPreview confirmed, TargetDeleteMode mode, CancellationToken cancellationToken)
        {
            return RunAsync(confirmed, mode, null, cancellationToken);
        }

        /// <summary>
        /// Выполнение подтверждённого предпросмотра с сообщениями о ходе (<paramref name="progress"/> может быть
        /// <c>null</c>).
        ///
        /// Отмена:
        ///  - во время чтения и записи манифеста — <see cref="OperationCanceledException"/>, удаления не было;
        ///  - во время удаления — после текущего пакета, результат <see cref="CleanupResult.Cancelled"/> с точными
        ///    счётчиками; всё удалённое перечислено в манифесте.
        /// </summary>
        public async Task<CleanupResult> RunAsync(
            CleanupPreview confirmed, TargetDeleteMode mode, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            if (confirmed is null)
            {
                throw new ArgumentNullException(nameof(confirmed));
            }

            if (!confirmed.CanProceed)
            {
                return CleanupResult.RefusedResult(confirmed.BlockReason ?? "Предпросмотр не допускает выполнения.");
            }

            CleanupPreview fresh = await BuildAsync(confirmed.Scope, confirmed.Filter, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!fresh.CanProceed)
            {
                return CleanupResult.RefusedResult(fresh.BlockReason ?? "Данные перед удалением неполные.");
            }

            Selection whole = Select(
                confirmed.Plan.ToDelete.Select(i => i.ItemId), fresh.Plan.ToDelete.Select(i => i.ItemId));
            Selection instances = Select(confirmed.Plan.OccurrenceIdsToDelete, fresh.Plan.OccurrenceIdsToDelete);
            int newSincePreview = whole.OnlyInFresh + instances.OnlyInFresh;
            int changedSincePreview = whole.OnlyInConfirmed + instances.OnlyInConfirmed;

            if (whole.Both.Count == 0 && instances.Both.Count == 0)
            {
                return CleanupResult.Success(0, 0, newSincePreview, changedSincePreview, manifestPath: null);
            }

            int total = whole.Both.Count + instances.Both.Count;
            progress?.Report(new CleanupProgress(CleanupStage.WritingManifest));
            string manifestPath;
            try
            {
                manifestPath = await _manifestWriter
                    .WriteAsync(ManifestOf(fresh.Plan, whole.Both.Concat(instances.Both)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return ManifestNotWritten(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ManifestNotWritten(ex);
            }

            // Итог неуспешной фазы (отмена или ошибка) с точными счётчиками — одно место для обеих фаз (DRY). Отказы
            // сервера по элементам из уже пройденной фазы сохраняются в счёте.
            CleanupResult Stopped(BatchDeleteOutcome outcome, int deleted, int trimmed, int refusedBefore, string fallbackError)
            {
                return outcome.IsCancelled
                    ? CleanupResult.CancelledResult(deleted, trimmed, newSincePreview, changedSincePreview, manifestPath)
                    : CleanupResult.Failed(
                        outcome.Error ?? fallbackError,
                        deleted,
                        trimmed,
                        newSincePreview,
                        changedSincePreview,
                        manifestPath,
                        outcome.Exception,
                        refusedBefore + outcome.RefusedCount);
            }

            progress?.Report(new CleanupProgress(CleanupStage.Deleting, 0, total));
            BatchDeleteOutcome deletion = await _executor
                .DeleteAllAsync(
                    whole.Both, _deleter, mode, snapshotIsAuthoritative: true, DeletionProgress(progress, 0, total), cancellationToken)
                .ConfigureAwait(false);

            // Отказ сервера по отдельным событиям не отменяет обрезку серий: список пройден, остальное удаляется дальше.
            if (!deletion.ReachedEnd)
            {
                return Stopped(deletion, deletion.RemovedCount, 0, 0, "Ошибка удаления.");
            }

            BatchDeleteOutcome trimming = await _executor
                .DeleteAllAsync(
                    instances.Both,
                    _deleter,
                    mode,
                    snapshotIsAuthoritative: true,
                    DeletionProgress(progress, deletion.RemovedCount, total),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!trimming.ReachedEnd)
            {
                return Stopped(trimming, deletion.RemovedCount, trimming.RemovedCount, deletion.RefusedCount, "Ошибка обрезки серий.");
            }

            int refused = deletion.RefusedCount + trimming.RefusedCount;
            if (refused > 0)
            {
                string reasons = string.Join(" ", new[] { deletion.Error, trimming.Error }.Where(e => !string.IsNullOrEmpty(e)));
                return CleanupResult.WithRefusals(
                    reasons + " Запустите очистку снова, чтобы повторить для них.",
                    deletion.RemovedCount,
                    trimming.RemovedCount,
                    refused,
                    newSincePreview,
                    changedSincePreview,
                    manifestPath);
            }

            return CleanupResult.Success(
                deletion.RemovedCount, trimming.RemovedCount, newSincePreview, changedSincePreview, manifestPath);
        }

        private async Task<CleanupPreview> BuildAsync(
            CleanupScope scope, CleanupFilter filter, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new CleanupProgress(CleanupStage.ReadingCalendar));
            IProgress<SnapshotReadProgress>? readProgress = progress is null
                ? null
                : new MappedProgress<SnapshotReadProgress>(
                    p => progress.Report(new CleanupProgress(CleanupStage.ReadingCalendar, p.ItemsRead, p.TotalItems)));
            TargetSnapshot snapshot = await _snapshotReader.ReadAsync(_pageReader, readProgress, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TargetItemInfo> items = snapshot.Items;
            IReadOnlyList<TargetOccurrence> occurrences = Array.Empty<TargetOccurrence>();
            string? blockReason = snapshot.IsAuthoritative
                ? null
                : snapshot.Completeness.Reason ?? "Снимок приёмника неполный — удаление недоступно.";

            if (!scope.IsWholeCalendar && snapshot.IsAuthoritative)
            {
                List<string> masterIds = items
                    .Where(i => i.Kind == ItemKind.RecurringMaster)
                    .Select(i => i.ItemId)
                    .ToList();

                if (masterIds.Count > 0)
                {
                    progress?.Report(new CleanupProgress(CleanupStage.ReadingSeries));
                    IReadOnlyDictionary<string, RecurrenceInfo> recurrence =
                        await _recurrenceReader.LoadAsync(masterIds, cancellationToken).ConfigureAwait(false);
                    items = RecurrenceEnricher.Apply(items, recurrence);
                    if (masterIds.Any(id => !recurrence.ContainsKey(id)))
                    {
                        // Удалять уже нельзя — раскрытие окна (тяжёлый запрос) не нужно: план показывается по правилам,
                        // а кнопка удаления заблокирована с этой причиной.
                        blockReason = "Не удалось прочитать границы части серий — удаление недоступно.";
                    }
                    else
                    {
                        progress?.Report(new CleanupProgress(CleanupStage.ReadingPeriod));
                        OccurrenceReadResult window = await _occurrenceReader
                            .ReadOccurrencesAsync(scope.WindowStartUtc, scope.WindowEndUtc, cancellationToken)
                            .ConfigureAwait(false);
                        if (!window.IsComplete)
                        {
                            blockReason = "Экземпляры серий в периоде прочитаны не полностью — удаление недоступно.";
                        }

                        occurrences = window.Occurrences;
                    }
                }
            }

            CleanupPlan plan = CleanupPlanner.BuildPlan(items, scope, filter, occurrences);
            return new CleanupPreview(scope, filter, blockReason is null, blockReason, plan);
        }

        /// <summary>
        /// Строки манифеста: тот же список, что показывал предпросмотр (<see cref="CleanupManifestEntry.ListOf"/>, DRY),
        /// оставлено только то, что уйдёт на удаление. Порядок — порядок удаления (целиком, затем экземпляры); данные —
        /// из свежего плана (актуальные тема и время). Идентификаторы EWS уникальны, поэтому общий фильтр по id верен.
        /// </summary>
        private static IReadOnlyList<CleanupManifestEntry> ManifestOf(CleanupPlan freshPlan, IEnumerable<string> deletedIds)
        {
            var keep = new HashSet<string>(deletedIds, StringComparer.Ordinal);
            return CleanupManifestEntry.ListOf(freshPlan).Where(e => keep.Contains(e.ItemId)).ToArray();
        }

        /// <summary>Пересчёт нарастающего итога пакета в общий ход удаления (вторая фаза идёт со смещением).</summary>
        private static IProgress<int>? DeletionProgress(IProgress<CleanupProgress>? progress, int offset, int total)
        {
            return progress is null
                ? null
                : new MappedProgress<int>(done => progress.Report(new CleanupProgress(CleanupStage.Deleting, offset + done, total)));
        }

        private static CleanupResult ManifestNotWritten(Exception error)
        {
            return CleanupResult.RefusedResult(
                "Манифест удаляемого не записан, поэтому удаление не выполнялось: " + error.Message);
        }

        private static Selection Select(IEnumerable<string> confirmedIds, IEnumerable<string> freshIds)
        {
            var confirmed = new HashSet<string>(confirmedIds, StringComparer.Ordinal);
            List<string> fresh = freshIds.Distinct(StringComparer.Ordinal).ToList();
            List<string> both = fresh.Where(confirmed.Contains).ToList();
            return new Selection(both, fresh.Count - both.Count, confirmed.Count - both.Count);
        }

        /// <summary>
        /// Синхронный переходник сообщений о ходе. Не <c>Progress&lt;T&gt;</c>: тот без контекста синхронизации
        /// публикует в пул потоков и может переставить сообщения; доставку в поток интерфейса делает внешний
        /// <c>Progress&lt;T&gt;</c>, созданный окном.
        /// </summary>
        private sealed class Selection
        {
            public Selection(IReadOnlyList<string> both, int onlyInFresh, int onlyInConfirmed)
            {
                Both = both;
                OnlyInFresh = onlyInFresh;
                OnlyInConfirmed = onlyInConfirmed;
            }

            public IReadOnlyList<string> Both { get; }

            public int OnlyInFresh { get; }

            public int OnlyInConfirmed { get; }
        }
    }
}
