using System;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Model;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>
    /// Чистое отображение извлечённых из EWS полей в доменную модель. Вынесено отдельно от сетевого кода,
    /// чтобы покрываться модульными тестами без сервера.
    /// </summary>
    public static class EwsItemMapper
    {
        public static ItemKind KindFromRecurringMaster(bool isRecurringMaster)
        {
            return isRecurringMaster ? ItemKind.RecurringMaster : ItemKind.Single;
        }

        public static TargetItemInfo ToTargetItem(
            string itemId,
            string? changeKey,
            string itemClass,
            bool isRecurringMaster,
            DateTime startUtc,
            DateTime endUtc,
            DateTime? firstOccurrenceUtc,
            DateTime? lastOccurrenceUtc,
            string? bridgeSourceId,
            string? subject,
            bool isAllDay = false)
        {
            return new TargetItemInfo(
                itemId,
                changeKey,
                itemClass,
                KindFromRecurringMaster(isRecurringMaster),
                startUtc,
                endUtc,
                isRecurringMaster ? firstOccurrenceUtc : null,
                isRecurringMaster ? lastOccurrenceUtc : null,
                bridgeSourceId,
                subject: subject,
                isAllDay: isAllDay);
        }

        /// <summary>
        /// Элемент раскрытого окна (CalendarView). Экземпляром серии считаются только повторение и исключение;
        /// одиночное событие в обрезку серий не попадает никогда.
        /// </summary>
        public static TargetOccurrence ToOccurrence(
            string occurrenceItemId,
            string? seriesUid,
            DateTime startUtc,
            DateTime endUtc,
            AppointmentType appointmentType,
            string? subject,
            bool isAllDay = false)
        {
            bool isSeriesInstance =
                appointmentType == AppointmentType.Occurrence || appointmentType == AppointmentType.Exception;
            return new TargetOccurrence(occurrenceItemId, seriesUid, startUtc, endUtc, isSeriesInstance, subject, isAllDay);
        }
    }
}
