using System;
using System.Collections.Generic;

namespace IwoBridge.Ews.Transport
{
    /// <summary>
    /// Часовой пояс для записи событий (PLAN.md §6.4, ловушка T31). Событие на весь день Exchange выравнивает по полуночи
    /// в поясе запроса, а правило повторения считает в поясе события: в UTC событие «на весь день» уехало бы на сутки, а
    /// серия в 00:30 местного времени — на другой день недели. Поэтому запись идёт в поясе календаря приёмника из
    /// профиля — с фиксированным смещением, без перехода на летнее время.
    ///
    /// Exchange 2007 ищет пояс по коду в реестре Windows сервера (код «UTC» в Windows Server 2003 не найден — проверено),
    /// поэтому код берётся из классических поясов Windows XP/2003 с тем же смещением и без летнего времени. Смещения без
    /// такого пояса (−8, −9, −2…) записываются в UTC: время событий точное, но событие «на весь день» может показаться
    /// на соседних сутках — об этом говорит <see cref="IsExact"/>.
    /// </summary>
    internal static class CalendarZones
    {
        // Только пояса без перехода на летнее время в классическом реестре Windows (XP/2003).
        private static readonly IReadOnlyDictionary<int, string> ByMinutes = new Dictionary<int, string>
        {
            [-12 * 60] = "Dateline Standard Time",
            [-11 * 60] = "Samoa Standard Time",
            [-10 * 60] = "Hawaiian Standard Time",
            [-7 * 60] = "US Mountain Standard Time",
            [-6 * 60] = "Central America Standard Time",
            [-5 * 60] = "SA Pacific Standard Time",
            [-4 * 60] = "SA Western Standard Time",
            [-3 * 60] = "SA Eastern Standard Time",
            [-1 * 60] = "Cape Verde Standard Time",
            [0] = "Greenwich Standard Time",
            [1 * 60] = "W. Central Africa Standard Time",
            [2 * 60] = "South Africa Standard Time",
            [3 * 60] = "Arab Standard Time",
            [4 * 60] = "Arabian Standard Time",
            [4 * 60 + 30] = "Afghanistan Standard Time",
            [5 * 60] = "West Asia Standard Time",
            [5 * 60 + 30] = "India Standard Time",
            [5 * 60 + 45] = "Nepal Standard Time",
            [6 * 60] = "Central Asia Standard Time",
            [6 * 60 + 30] = "Myanmar Standard Time",
            [7 * 60] = "SE Asia Standard Time",
            [8 * 60] = "Singapore Standard Time",
            [9 * 60] = "Tokyo Standard Time",
            [9 * 60 + 30] = "AUS Central Standard Time",
            [10 * 60] = "West Pacific Standard Time",
            [11 * 60] = "Central Pacific Standard Time",
            [12 * 60] = "Fiji Standard Time",
            [13 * 60] = "Tonga Standard Time",
        };

        /// <summary>Пояс записи для смещения календаря приёмника.</summary>
        public static TimeZoneInfo ForOffset(TimeSpan offset)
        {
            string id = ByMinutes.TryGetValue((int)offset.TotalMinutes, out string? known) ? known : ByMinutes[0];
            TimeSpan actual = known is null ? TimeSpan.Zero : offset;
            return TimeZoneInfo.CreateCustomTimeZone(id, actual, id, id);
        }

        /// <summary>Есть ли для смещения точный пояс (иначе запись идёт в UTC).</summary>
        public static bool IsExact(TimeSpan offset) => ByMinutes.ContainsKey((int)offset.TotalMinutes);
    }
}
