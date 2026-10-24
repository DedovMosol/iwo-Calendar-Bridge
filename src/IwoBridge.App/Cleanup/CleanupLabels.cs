using System;
using IwoBridge.Core.Cleanup;
using IwoBridge.Core.Model;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Названия видов элементов и причин пропуска для человека — одно место на продукт (DRY): одни и те же слова
    /// в окне предпросмотра и в манифесте, чтобы пользователь сверял их без перевода.
    /// </summary>
    public static class CleanupLabels
    {
        /// <summary>Вид строки удаляемого.</summary>
        public static string Of(CleanupManifestEntryKind kind)
        {
            switch (kind)
            {
                case CleanupManifestEntryKind.Single:
                    return "Событие";
                case CleanupManifestEntryKind.WholeSeries:
                    return "Серия целиком";
                case CleanupManifestEntryKind.SeriesInstance:
                    return "Экземпляр серии";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид строки манифеста.");
            }
        }

        /// <summary>Вид элемента, который остаётся в календаре.</summary>
        public static string KeptItemKind(TargetItemInfo item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (!item.IsAppointment)
            {
                return "Не событие";
            }

            return item.Kind == ItemKind.RecurringMaster ? "Серия" : "Событие";
        }

        /// <summary>Причина, по которой элемент не удаляется.</summary>
        public static string Of(SkipReason reason)
        {
            switch (reason)
            {
                case SkipReason.OutsidePeriod:
                    return "Вне периода";
                case SkipReason.NotBridgeCopy:
                    return "Создано не программой";
                case SkipReason.NonAppointmentClass:
                    return "Не событие календаря — не трогаем";
                case SkipReason.SeriesUidUnavailable:
                    return "Серия на границе периода без идентификатора — экземпляры не сопоставить надёжно";
                case SkipReason.AmbiguousSeriesUid:
                    return "Идентификатор совпадает с сохраняемой серией — экземпляры не различить";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, "Неизвестная причина пропуска.");
            }
        }
    }
}
