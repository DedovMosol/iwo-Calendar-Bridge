namespace IwoBridge.Core.Model
{
    /// <summary>Способ удаления в приёмнике (нейтрально к EWS).</summary>
    public enum TargetDeleteMode
    {
        /// <summary>Перенести в «Удалённые» — восстановимо.</summary>
        MoveToDeletedItems,

        /// <summary>Удалить из хранилища безвозвратно.</summary>
        HardDelete,
    }
}
