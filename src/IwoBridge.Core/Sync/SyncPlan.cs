using System;
using System.Collections.Generic;
using System.Linq;

namespace IwoBridge.Core.Sync
{
    /// <summary>Что сделать с копией.</summary>
    public enum SyncActionKind
    {
        /// <summary>Создать копию события источника.</summary>
        Create,

        /// <summary>Перезаписать содержимое существующей копии (тот же элемент приёмника).</summary>
        Update,

        /// <summary>
        /// Пересоздать копию: удалить и создать заново. Когда событие стало серией или серия — одиночным событием:
        /// EWS не превращает одно в другое на месте надёжно.
        /// </summary>
        Recreate,

        /// <summary>Удалить копию.</summary>
        Delete,
    }

    /// <summary>Почему.</summary>
    public enum SyncReason
    {
        /// <summary>Событие появилось в источнике, копии нет.</summary>
        NewInSource,

        /// <summary>Содержимое события изменилось (отпечатки различаются).</summary>
        ChangedInSource,

        /// <summary>У копии нет отпечатка (создана старой версией) — перезаписывается один раз.</summary>
        UnknownCopyVersion,

        /// <summary>Событие стало серией или наоборот.</summary>
        KindChanged,

        /// <summary>Событие удалено в источнике.</summary>
        DeletedInSource,

        /// <summary>Лишняя копия того же события (например, после сбоя посреди записи).</summary>
        DuplicateCopy,
    }

    /// <summary>Одно действие плана.</summary>
    public sealed class SyncAction
    {
        internal SyncAction(SyncActionKind kind, SyncReason reason, SourceEvent? source, BridgeCopy? copy)
        {
            Kind = kind;
            Reason = reason;
            Source = source;
            Copy = copy;
        }

        public SyncActionKind Kind { get; }

        public SyncReason Reason { get; }

        /// <summary>Событие источника (нет у удаления).</summary>
        public SourceEvent? Source { get; }

        /// <summary>Копия (нет у создания).</summary>
        public BridgeCopy? Copy { get; }

        /// <summary>Идентификатор события источника, к которому относится действие.</summary>
        public string SourceId => Source?.SourceId ?? Copy!.SourceId;
    }

    /// <summary>
    /// План точечной синхронизации за период: только разница между источником и копиями. Применять — только если
    /// <see cref="CanApply"/>; иначе план годится лишь для показа (почему — в <see cref="BlockReason"/>).
    /// </summary>
    public sealed class SyncPlan
    {
        internal SyncPlan(
            IReadOnlyList<SyncAction> actions,
            int unchangedCount,
            IReadOnlyList<string> conflictingSourceIds,
            string? blockReason,
            string? deletesSuppressedReason)
        {
            Actions = actions;
            UnchangedCount = unchangedCount;
            ConflictingSourceIds = conflictingSourceIds;
            BlockReason = blockReason;
            DeletesSuppressedReason = deletesSuppressedReason;
        }

        /// <summary>Действия в порядке применения: удаления лишнего, пересоздания, перезаписи, создания.</summary>
        public IReadOnlyList<SyncAction> Actions { get; }

        /// <summary>Копий без изменений — не трогаются.</summary>
        public int UnchangedCount { get; }

        /// <summary>
        /// Идентификаторы, встреченные в источнике больше одного раза (ошибка адаптера или данных). По ним ничего
        /// не делается: неясно, какое из событий копировать.
        /// </summary>
        public IReadOnlyList<string> ConflictingSourceIds { get; }

        /// <summary>Почему план нельзя применять (подозрение на массовое удаление); <c>null</c> — можно.</summary>
        public string? BlockReason { get; }

        /// <summary>
        /// Источник прочитан не полностью: удаления «удалено в источнике» не включены в план (отсутствие события в
        /// неполном снимке ничего не доказывает); создания и перезаписи — включены. <c>null</c> — источник полный.
        /// </summary>
        public string? DeletesSuppressedReason { get; }

        public bool CanApply => BlockReason is null;

        public bool IsEmpty => Actions.Count == 0;

        public int Count(SyncActionKind kind) => Actions.Count(a => a.Kind == kind);
    }

    /// <summary>
    /// Пороги защиты от массового удаления. Удаления «удалено в источнике» подозрительны,
    /// если их больше <see cref="MaxDeletes"/> за прогон или больше доли <see cref="MaxDeleteShare"/> копий периода
    /// (доля проверяется, только когда удалений больше <see cref="ShareFloor"/>: 2 удаления из 5 копий —
    /// обычная правка, а не сбой). Подозрительный план не применяется без явного подтверждения.
    /// </summary>
    public sealed class SyncOptions
    {
        public static readonly SyncOptions Default = new SyncOptions();

        /// <param name="maxDeletes">Больше стольких удалений за прогон — только с подтверждением.</param>
        /// <param name="maxDeleteShare">Доля копий периода (0…1], удаление сверх которой — только с подтверждением.</param>
        /// <param name="shareFloor">До скольких удалений доля не проверяется.</param>
        /// <param name="allowMassDelete">Пользователь подтвердил массовое удаление явно.</param>
        public SyncOptions(int maxDeletes = 20, double maxDeleteShare = 0.1, int shareFloor = 5, bool allowMassDelete = false)
        {
            if (maxDeletes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDeletes));
            }

            if (!(maxDeleteShare > 0 && maxDeleteShare <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(maxDeleteShare));
            }

            if (shareFloor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(shareFloor));
            }

            MaxDeletes = maxDeletes;
            MaxDeleteShare = maxDeleteShare;
            ShareFloor = shareFloor;
            AllowMassDelete = allowMassDelete;
        }

        public int MaxDeletes { get; }

        public double MaxDeleteShare { get; }

        public int ShareFloor { get; }

        public bool AllowMassDelete { get; }
    }
}
