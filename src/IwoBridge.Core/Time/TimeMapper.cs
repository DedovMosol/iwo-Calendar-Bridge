using System;

namespace IwoBridge.Core.Time
{
    /// <summary>
    /// Нормализация <see cref="DateTime"/> к UTC — единственное место, где это делается (SoC).
    /// Зависит только от <see cref="DateTime.Kind"/>, не от организации: никаких зашитых поясов и
    /// значений. Привязка к местному поясу приёмника вынесена в <see cref="TargetTimeZone"/>,
    /// а конкретное смещение задаётся конфигурацией (PLAN.md §0, T36; §6.4, T31).
    /// </summary>
    public static class TimeMapper
    {
        /// <summary>
        /// Приводит значение к UTC: Utc — как есть, Local — конвертируется, Unspecified трактуется как UTC.
        /// Трактовка Unspecified как UTC — протокольное решение (EWS без указания зоны считает время UTC),
        /// а не данные организации.
        /// </summary>
        public static DateTime ToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    return value;
                case DateTimeKind.Local:
                    return value.ToUniversalTime();
                default:
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }
    }
}
