using System;
using IwoBridge.Core.Cleanup;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Результат пробного прогона очистки (без удаления): объём и фильтр, по которым он построен, вердикт
    /// «можно ли выполнять» и план. Подтверждённый пользователем предпросмотр передаётся в
    /// <see cref="CleanupRunner.RunAsync"/>: удаляется только то, что было в нём показано.
    /// </summary>
    public sealed class CleanupPreview
    {
        public CleanupPreview(
            CleanupScope scope, CleanupFilter filter, bool canProceed, string? blockReason, CleanupPlan plan)
        {
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Filter = filter;
            CanProceed = canProceed;
            BlockReason = blockReason;
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        }

        public CleanupScope Scope { get; }

        public CleanupFilter Filter { get; }

        /// <summary>Данные полны, операцию можно выполнять.</summary>
        public bool CanProceed { get; }

        /// <summary>Причина, по которой выполнять нельзя (при <see cref="CanProceed"/> == false).</summary>
        public string? BlockReason { get; }

        /// <summary>План: что удалить целиком, какие серии обрезать, что пропущено.</summary>
        public CleanupPlan Plan { get; }
    }
}
