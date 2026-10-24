using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Model;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Cleanup
{
    /// <summary>
    /// Чистая функция планирования очистки: снимок приёмника + объём + фильтр (+ раскрытые экземпляры окна
    /// в режиме «период») → план. Без сети и побочных эффектов (PLAN.md §8).
    ///
    /// Режим «период» (принадлежность окну — единое правило <see cref="TimeWindow.Overlaps"/>):
    ///  - одиночное событие удаляется, если пересекает период;
    ///  - серия удаляется целиком, если в периоде лежат все её экземпляры: повторения по правилу (от первого
    ///    до последнего) и каждое изменённое повторение;
    ///  - серия пропускается, если в периоде нет её экземпляров ни по правилу, ни по факту (раскрытое окно,
    ///    изменённые повторения);
    ///  - остальные серии обрезаются: удаляются только их экземпляры, пересекающие период.
    /// Экземпляры сопоставляются с серией по UID и только среди повторений и исключений — одиночные события
    /// в обрезку не попадают. Если UID серии неизвестен или совпадает с UID сохраняемой серии, обрезка не
    /// выполняется и серия уходит в пропущенные с причиной: чужие события не удаляются никогда, а пропуск
    /// виден в предпросмотре.
    /// </summary>
    public static class CleanupPlanner
    {
        private enum Decision
        {
            Delete,
            Trim,
            Skip,
        }

        public static CleanupPlan BuildPlan(
            IReadOnlyCollection<TargetItemInfo> items,
            CleanupScope scope,
            CleanupFilter filter,
            IReadOnlyCollection<TargetOccurrence>? occurrencesInWindow)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (scope is null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (!scope.IsWholeCalendar && occurrencesInWindow is null)
            {
                throw new ArgumentNullException(
                    nameof(occurrencesInWindow), "В режиме «период» нужны раскрытые экземпляры окна.");
            }

            // Неизвестное значение фильтра не должно молча означать «все события»: отказ в безопасную сторону.
            if (filter != CleanupFilter.AllEvents && filter != CleanupFilter.BridgeCopiesOnly)
            {
                throw new ArgumentOutOfRangeException(nameof(filter), filter, "Неизвестный фильтр очистки.");
            }

            IReadOnlyCollection<TargetOccurrence> occurrences = occurrencesInWindow ?? Array.Empty<TargetOccurrence>();

            var seriesUidsInWindow = new HashSet<string>(StringComparer.Ordinal);
            if (!scope.IsWholeCalendar)
            {
                foreach (TargetOccurrence occurrence in occurrences)
                {
                    if (IsSeriesInstanceInWindow(occurrence, scope))
                    {
                        seriesUidsInWindow.Add(occurrence.SeriesUid!);
                    }
                }
            }

            var toDelete = new List<TargetItemInfo>();
            var trimCandidates = new List<TargetItemInfo>();
            var skipped = new List<SkippedItem>();

            // UID серий, которые не обрезаются: сохраняемых (их экземпляры трогать нельзя) и удаляемых целиком (их
            // экземпляры уйдут вместе с мастером — обрезка того же UID дала бы двойной учёт и дубли в манифесте).
            var preservedSeriesUids = new HashSet<string>(StringComparer.Ordinal);
            var deletedSeriesUids = new HashSet<string>(StringComparer.Ordinal);

            foreach (TargetItemInfo item in items)
            {
                if (item is null)
                {
                    throw new ArgumentException("Снимок содержит null-элемент.", nameof(items));
                }

                Decision decision = Decide(item, scope, filter, seriesUidsInWindow, out SkipReason reason);
                if (decision == Decision.Delete)
                {
                    toDelete.Add(item);
                    if (item.Kind == ItemKind.RecurringMaster && item.Uid != null)
                    {
                        deletedSeriesUids.Add(item.Uid);
                    }
                }
                else if (decision == Decision.Trim)
                {
                    trimCandidates.Add(item);
                }
                else
                {
                    skipped.Add(new SkippedItem(item, reason));
                    if (item.Kind == ItemKind.RecurringMaster && item.Uid != null)
                    {
                        preservedSeriesUids.Add(item.Uid);
                    }
                }
            }

            var seriesToTrim = new List<TargetItemInfo>();
            var trimUids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TargetItemInfo series in trimCandidates)
            {
                if (series.Uid is null)
                {
                    skipped.Add(new SkippedItem(series, SkipReason.SeriesUidUnavailable));
                }
                else if (preservedSeriesUids.Contains(series.Uid) || deletedSeriesUids.Contains(series.Uid))
                {
                    skipped.Add(new SkippedItem(series, SkipReason.AmbiguousSeriesUid));
                }
                else
                {
                    seriesToTrim.Add(series);
                    trimUids.Add(series.Uid);
                }
            }

            var occurrencesToDelete = new List<TargetOccurrence>();
            if (trimUids.Count > 0)
            {
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (TargetOccurrence occurrence in occurrences)
                {
                    if (IsSeriesInstanceInWindow(occurrence, scope)
                        && trimUids.Contains(occurrence.SeriesUid!)
                        && seenIds.Add(occurrence.OccurrenceItemId))
                    {
                        occurrencesToDelete.Add(occurrence);
                    }
                }
            }

            return new CleanupPlan(toDelete, seriesToTrim, occurrencesToDelete, skipped);
        }

        private static Decision Decide(
            TargetItemInfo item,
            CleanupScope scope,
            CleanupFilter filter,
            HashSet<string> seriesUidsInWindow,
            out SkipReason reason)
        {
            reason = SkipReason.OutsidePeriod;

            if (!item.IsAppointment)
            {
                reason = SkipReason.NonAppointmentClass;
                return Decision.Skip;
            }

            if (filter == CleanupFilter.BridgeCopiesOnly && !item.IsBridgeCopy)
            {
                reason = SkipReason.NotBridgeCopy;
                return Decision.Skip;
            }

            if (scope.IsWholeCalendar)
            {
                return Decision.Delete;
            }

            switch (item.Kind)
            {
                case ItemKind.Single:
                    return TimeWindow.Overlaps(item.StartUtc, item.EndUtc, scope.WindowStartUtc, scope.WindowEndUtc)
                        ? Decision.Delete
                        : Decision.Skip;

                case ItemKind.RecurringMaster:
                    return DecideSeries(item, scope, seriesUidsInWindow);

                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Неизвестный вид элемента.");
            }
        }

        private static Decision DecideSeries(TargetItemInfo master, CleanupScope scope, HashSet<string> seriesUidsInWindow)
        {
            DateTime windowStart = scope.WindowStartUtc;
            DateTime windowEnd = scope.WindowEndUtc;
            DateTime first = master.FirstOccurrenceUtc ?? master.StartUtc;

            // Бессрочная серия: последнего повторения нет — серия тянется вперёд бесконечно.
            DateTime lastStart = master.LastOccurrenceUtc ?? DateTime.MaxValue;

            // Длительность повторения — по мастеру. Без неё серию, последнее повторение которой начинается
            // до периода, но заканчивается в нём, приняли бы за «вне периода» и оставили бы мусор.
            TimeSpan duration = master.EndUtc > master.StartUtc ? master.EndUtc - master.StartUtc : TimeSpan.Zero;
            DateTime lastEnd = lastStart == DateTime.MaxValue ? DateTime.MaxValue : SafeAdd(lastStart, duration);

            bool everyModifiedInside = master.ModifiedOccurrences.All(
                span => TimeWindow.Overlaps(span.StartUtc, span.EndUtc, windowStart, windowEnd));
            bool anyModifiedInside = master.ModifiedOccurrences.Any(
                span => TimeWindow.Overlaps(span.StartUtc, span.EndUtc, windowStart, windowEnd));

            // Серия целиком в периоде — если в период попадают первое и последнее повторение по правилу (по единому
            // правилу TimeWindow.Overlaps: и то, что начинается до периода и заканчивается в нём, — «в периоде»;
            // повторения между ними тогда попадают тоже) и каждое изменённое повторение. Исключение, перенесённое за
            // пределы периода, запрещает удалять серию целиком: вместе с мастером исчезло бы событие вне периода.
            bool firstInside = TimeWindow.Overlaps(first, SafeAdd(first, duration), windowStart, windowEnd);
            bool lastInside = lastStart != DateTime.MaxValue && TimeWindow.Overlaps(lastStart, lastEnd, windowStart, windowEnd);
            if (firstInside && lastInside && everyModifiedInside)
            {
                return Decision.Delete;
            }

            if (master.Uid != null)
            {
                // UID известен — решает факт из раскрытого окна: есть экземпляры в периоде (в том числе перенесённое
                // туда исключение) — серия граничная, нет — вне периода, даже если границы правила его охватывают
                // (например, ежегодная серия и период в неделю). Окно полное: иначе сценарий запрещает удаление.
                return seriesUidsInWindow.Contains(master.Uid) || anyModifiedInside ? Decision.Trim : Decision.Skip;
            }

            // UID неизвестен — экземпляры не сопоставить; по правилу решаем, граничная ли серия, чтобы в предпросмотре
            // была понятная причина пропуска («нет идентификатора»), а не «вне периода».
            bool noneByRule = lastEnd <= windowStart || first >= windowEnd;
            return noneByRule && !anyModifiedInside ? Decision.Skip : Decision.Trim;
        }

        private static bool IsSeriesInstanceInWindow(TargetOccurrence occurrence, CleanupScope scope)
        {
            return occurrence.IsSeriesInstance
                && occurrence.SeriesUid != null
                && TimeWindow.Overlaps(occurrence.StartUtc, occurrence.EndUtc, scope.WindowStartUtc, scope.WindowEndUtc);
        }

        /// <summary>Сложение даты и длительности с защитой от переполнения (возвращает MaxValue).</summary>
        private static DateTime SafeAdd(DateTime value, TimeSpan delta)
        {
            long remaining = DateTime.MaxValue.Ticks - value.Ticks;
            if (delta.Ticks >= remaining)
            {
                return DateTime.MaxValue;
            }

            return new DateTime(value.Ticks + delta.Ticks, value.Kind);
        }
    }
}
