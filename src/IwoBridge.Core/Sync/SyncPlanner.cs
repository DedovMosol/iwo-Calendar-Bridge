using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Точечная синхронизация за период («перезапись периода — не полная, а по разнице»). Чистая функция: на входе
    /// снимок источника, копии моста в приёмнике и период; на выходе — план. Ни сети, ни Outlook.
    ///
    /// Правила (каждое — тест в SyncPlannerTests):
    ///  1. Копия опознаётся только по метке источника (<c>IwoBridgeSourceId</c>). Чужие события приёмника сюда не
    ///     попадают и не трогаются никогда.
    ///  2. Событие источника в периоде без копии → создать.
    ///  3. Копия есть, отпечатки равны → не трогать; различаются или у копии нет отпечатка → перезаписать; вид
    ///     сменился (одиночное ↔ серия) → пересоздать. Событие, перенесённое за пределы периода, но присутствующее
    ///     в снимке (см. контракт <see cref="SourceSnapshot"/>), тоже перезаписывается — копия переезжает вслед.
    ///     Копия события из периода, лежащая вне периода (событие перенесли в период), находится по метке и
    ///     перезаписывается, а не дублируется.
    ///  4. Несколько копий одного события → одна сохраняется (с совпадающим отпечатком, иначе первая по ItemId —
    ///     детерминированно), остальные удаляются как лишние.
    ///  5. Копия в периоде, события которой нет в снимке → удалить (удалено в источнике). Копии вне периода без
    ///     события не трогаются: о них этот снимок ничего не знает.
    ///  6. Неполный снимок → удаления по правилу 5 не планируются вовсе.
    ///  7. Защита от массового удаления (<see cref="SyncOptions"/>): пустой источник при непустых копиях, больше
    ///     20 удалений за прогон или больше 10 % копий периода → план не применяется без явного подтверждения (так
    ///     выглядит ошибка чтения — не тот ящик, пустая папка, сбой фильтра).
    ///  8. Повторяющийся идентификатор в источнике → по нему ничего не делается, он перечисляется в конфликтах.
    /// Серия — единица: сравнивается и переписывается целиком (мастер с правилом и исключениями).
    /// </summary>
    public static class SyncPlanner
    {
        public static SyncPlan Plan(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            SourceSnapshot source,
            IEnumerable<BridgeCopy> copies,
            SyncOptions? options = null)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (copies is null)
            {
                throw new ArgumentNullException(nameof(copies));
            }

            DateTime start = TimeMapper.ToUtc(windowStartUtc);
            DateTime end = TimeMapper.ToUtc(windowEndUtc);
            if (end <= start)
            {
                throw new ArgumentException("Конец периода должен быть позже начала.", nameof(windowEndUtc));
            }

            options ??= SyncOptions.Default;
            BridgeCopy[] allCopies = copies.ToArray();

            // Правило 8: повторяющиеся идентификаторы источника.
            string[] conflicts = source.Events
                .GroupBy(e => e.SourceId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var conflictSet = new HashSet<string>(conflicts, StringComparer.Ordinal);
            Dictionary<string, SourceEvent> sources = source.Events
                .Where(e => !conflictSet.Contains(e.SourceId))
                .ToDictionary(e => e.SourceId, StringComparer.Ordinal);
            ILookup<string, BridgeCopy> copiesById = allCopies.ToLookup(c => c.SourceId, StringComparer.Ordinal);

            var duplicates = new List<SyncAction>();
            var recreates = new List<SyncAction>();
            var updates = new List<SyncAction>();
            var creates = new List<SyncAction>();
            var deletes = new List<SyncAction>();
            int unchanged = 0;

            foreach (SourceEvent item in sources.Values)
            {
                BridgeCopy[] own = copiesById[item.SourceId].ToArray();
                if (own.Length == 0)
                {
                    // Событие вне периода попадает в снимок только как адресат копии (контракт снимка); без копии
                    // оно вне задачи этого периода.
                    if (item.Overlaps(start, end))
                    {
                        creates.Add(new SyncAction(SyncActionKind.Create, SyncReason.NewInSource, item, null));
                    }

                    continue;
                }

                BridgeCopy keep = ChooseCopy(own, item.ContentHash);
                foreach (BridgeCopy extra in own.Where(c => !ReferenceEquals(c, keep)))
                {
                    duplicates.Add(new SyncAction(SyncActionKind.Delete, SyncReason.DuplicateCopy, item, extra));
                }

                if (keep.Kind != item.Kind)
                {
                    recreates.Add(new SyncAction(SyncActionKind.Recreate, SyncReason.KindChanged, item, keep));
                }
                else if (keep.ContentHash is null)
                {
                    updates.Add(new SyncAction(SyncActionKind.Update, SyncReason.UnknownCopyVersion, item, keep));
                }
                else if (!string.Equals(keep.ContentHash, item.ContentHash, StringComparison.Ordinal))
                {
                    updates.Add(new SyncAction(SyncActionKind.Update, SyncReason.ChangedInSource, item, keep));
                }
                else
                {
                    unchanged++;
                }
            }

            // Правило 5–6: копии периода без события в снимке.
            string? suppressed = source.IsComplete ? null : source.IncompleteReason;
            BridgeCopy[] copiesInWindow = allCopies.Where(c => c.Overlaps(start, end)).ToArray();
            if (source.IsComplete)
            {
                foreach (BridgeCopy orphan in copiesInWindow.Where(c => !sources.ContainsKey(c.SourceId) && !conflictSet.Contains(c.SourceId)))
                {
                    deletes.Add(new SyncAction(SyncActionKind.Delete, SyncReason.DeletedInSource, null, orphan));
                }
            }

            // Пересоздание удаляет прежнюю копию: серия, пересоздаваемая одиночным событием, теряет все повторения, кроме
            // первого. Массовое пересоздание (например, источник вдруг перестал отдавать правила серий) — тот же риск,
            // что массовое удаление, и защищается тем же порогом.
            string? blockReason = options.AllowMassDelete ? null : MassDeleteReason(source, copiesInWindow.Length, deletes.Count + recreates.Count, options);

            // Порядок применения: сначала убрать лишнее и пересоздать (освобождает место и метки), затем
            // перезаписать, затем создать. Внутри — по времени, чтобы ход работы был понятен человеку.
            SyncAction[] actions = duplicates.Concat(deletes).OrderBy(Start).ThenBy(a => a.Copy!.ItemId, StringComparer.Ordinal)
                .Concat(recreates.OrderBy(Start))
                .Concat(updates.OrderBy(Start))
                .Concat(creates.OrderBy(Start).ThenBy(a => a.SourceId, StringComparer.Ordinal))
                .ToArray();

            return new SyncPlan(actions, unchanged, conflicts, blockReason, suppressed);
        }

        /// <summary>Какую из копий одного события оставить: с актуальным отпечатком, иначе первую по ItemId.</summary>
        private static BridgeCopy ChooseCopy(BridgeCopy[] copies, string hash)
        {
            if (copies.Length == 1)
            {
                return copies[0];
            }

            IOrderedEnumerable<BridgeCopy> ordered = copies.OrderBy(c => c.ItemId, StringComparer.Ordinal);
            return ordered.FirstOrDefault(c => string.Equals(c.ContentHash, hash, StringComparison.Ordinal)) ?? ordered.First();
        }

        private static string? MassDeleteReason(SourceSnapshot source, int copiesInWindow, int deletes, SyncOptions options)
        {
            if (deletes == 0)
            {
                return null;
            }

            if (source.Events.Count == 0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Источник за период пуст, а копий в приёмнике {0}: так выглядит ошибка чтения (не тот ящик или папка). Удаление всех копий — только после явного подтверждения.",
                    copiesInWindow);
            }

            if (deletes > options.MaxDeletes)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Удаляется или пересоздаётся {0} копий — больше {1} за один прогон. Если это ожидаемо, подтвердите удаление явно.",
                    deletes,
                    options.MaxDeletes);
            }

            if (deletes > options.ShareFloor && deletes > copiesInWindow * options.MaxDeleteShare)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Из {0} копий периода удаляется или пересоздаётся {1} — больше {2:0%}. Если это ожидаемо, подтвердите удаление явно.",
                    copiesInWindow,
                    deletes,
                    options.MaxDeleteShare);
            }

            return null;
        }

        private static DateTime Start(SyncAction action) => action.Source?.SpanStartUtc ?? action.Copy!.SpanStartUtc;
    }
}
