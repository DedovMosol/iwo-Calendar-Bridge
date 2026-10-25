using System;
using Microsoft.Exchange.WebServices.Data;
using IwoBridge.Core.Sync;

namespace IwoBridge.Ews.Mapping
{
    /// <summary>Занятость и конфиденциальность: ядро ↔ EWS. Чистые функции (DRY для чтения и записи).</summary>
    public static class EwsFieldMapper
    {
        public static LegacyFreeBusyStatus ToEws(FreeBusy busy)
        {
            switch (busy)
            {
                case FreeBusy.Free:
                    return LegacyFreeBusyStatus.Free;
                case FreeBusy.Tentative:
                    return LegacyFreeBusyStatus.Tentative;
                case FreeBusy.OutOfOffice:
                    return LegacyFreeBusyStatus.OOF;
                default:
                    return LegacyFreeBusyStatus.Busy;
            }
        }

        public static FreeBusy FromEws(LegacyFreeBusyStatus status)
        {
            switch (status)
            {
                case LegacyFreeBusyStatus.Free:
                    return FreeBusy.Free;
                case LegacyFreeBusyStatus.Tentative:
                    return FreeBusy.Tentative;
                case LegacyFreeBusyStatus.OOF:
                    return FreeBusy.OutOfOffice;
                default:
                    return FreeBusy.Busy;
            }
        }

        public static Sensitivity ToEws(ItemSensitivity sensitivity)
        {
            switch (sensitivity)
            {
                case ItemSensitivity.Personal:
                    return Sensitivity.Personal;
                case ItemSensitivity.Private:
                    return Sensitivity.Private;
                case ItemSensitivity.Confidential:
                    return Sensitivity.Confidential;
                default:
                    return Sensitivity.Normal;
            }
        }

        public static ItemSensitivity FromEws(Sensitivity sensitivity)
        {
            switch (sensitivity)
            {
                case Sensitivity.Personal:
                    return ItemSensitivity.Personal;
                case Sensitivity.Private:
                    return ItemSensitivity.Private;
                case Sensitivity.Confidential:
                    return ItemSensitivity.Confidential;
                default:
                    return ItemSensitivity.Normal;
            }
        }

        /// <summary>
        /// Начало и конец для записи: обычное событие — точные моменты UTC; событие «на весь день» — полночь местных дат
        /// календаря приёмника (значение без зоны, сервер трактует его в поясе записи — <see cref="Transport.CalendarZones"/>).
        /// </summary>
        public static (DateTime Start, DateTime End) TimesForWrite(DateTime startUtc, DateTime endUtc, bool isAllDay, TimeZoneInfo calendarZone)
        {
            if (calendarZone is null)
            {
                throw new ArgumentNullException(nameof(calendarZone));
            }

            if (!isAllDay)
            {
                return (DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), DateTime.SpecifyKind(endUtc, DateTimeKind.Utc));
            }

            DateTime start = NearestMidnight(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), calendarZone));
            DateTime end = NearestMidnight(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), calendarZone));
            if (end <= start)
            {
                end = start < DateTime.MaxValue.Date ? start.AddDays(1) : DateTime.MaxValue;
            }

            return (DateTime.SpecifyKind(start, DateTimeKind.Unspecified), DateTime.SpecifyKind(end, DateTimeKind.Unspecified));
        }

        /// <summary>
        /// Ближайшая полночь. Событие «на весь день» начинается в полночь пояса источника; в поясе приёмника это может быть
        /// 22:00 накануне (источник восточнее) или 02:00 (западнее). Отбрасывание времени сдвинуло бы первое на день
        /// раньше, ближайшая полночь даёт ту же дату при разнице поясов меньше 12 часов.
        /// </summary>
        private static DateTime NearestMidnight(DateTime local)
        {
            return local.TimeOfDay >= TimeSpan.FromHours(12) && local.Date < DateTime.MaxValue.Date ? local.Date.AddDays(1) : local.Date;
        }
    }
}
