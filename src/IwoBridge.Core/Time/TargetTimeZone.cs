using System;
using System.Globalization;

namespace IwoBridge.Core.Time
{
    /// <summary>
    /// Местный часовой пояс приёмника как фиксированное смещение от UTC без перехода на летнее время.
    /// Значение смещения приходит из конфигурации организации (не зашито в код — T36).
    ///
    /// Фиксированное смещение вместо <see cref="TimeZoneInfo"/> по имени выбрано сознательно: таблицы
    /// поясов на старых серверах (например, Windows Server 2003) могут не содержать актуальных правил,
    /// а имя пояса на клиенте и сервере может резолвиться по-разному. Смещение детерминировано и
    /// не зависит от версии ОС (PLAN.md §6.4, T31). Пояса с переходом на летнее время — вне текущего
    /// объёма (YAGNI); при необходимости добавляются отдельной реализацией абстракции времени.
    /// </summary>
    public sealed class TargetTimeZone
    {
        private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

        private TargetTimeZone(TimeSpan offset)
        {
            Offset = offset;
        }

        /// <summary>Смещение от UTC (положительное — к востоку).</summary>
        public TimeSpan Offset { get; }

        /// <summary>UTC без смещения — нейтральное значение по умолчанию, не привязанное к организации.</summary>
        public static TargetTimeZone Utc { get; } = new TargetTimeZone(TimeSpan.Zero);

        /// <remarks>
        /// Имя параметра совпадает с параметром <see cref="Configuration.ConnectionProfile"/> и полем настроек
        /// <c>utcOffset</c>: по <see cref="ArgumentException.ParamName"/> загрузчик профиля указывает поле с ошибкой.
        /// </remarks>
        public static TargetTimeZone FromOffset(TimeSpan utcOffset)
        {
            if (utcOffset < -MaxOffset || utcOffset > MaxOffset)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(utcOffset), utcOffset, "Смещение пояса должно быть в пределах ±14 часов.");
            }

            if (utcOffset.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("Смещение пояса должно быть кратно минуте.", nameof(utcOffset));
            }

            return new TargetTimeZone(utcOffset);
        }

        public static TargetTimeZone FromHours(double hours)
        {
            return FromOffset(TimeSpan.FromHours(hours));
        }

        /// <summary>Смещение в виде «±ЧЧ:ММ» — так оно хранится в настройках и показывается в окне.</summary>
        public string OffsetText => FormatOffset(Offset);

        /// <summary>«±ЧЧ:ММ» для смещения; ноль — «+00:00». Смещение вне ±14 часов не форматируется (оно недопустимо).</summary>
        public static string FormatOffset(TimeSpan offset)
        {
            if (offset < -MaxOffset || offset > MaxOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Смещение пояса должно быть в пределах ±14 часов.");
            }

            TimeSpan magnitude = offset.Duration();
            return (offset < TimeSpan.Zero ? "-" : "+") + magnitude.Hours.ToString("00", CultureInfo.InvariantCulture)
                + ":" + magnitude.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Строгий разбор «±ЧЧ:ММ» (знак обязателен, ровно две цифры часов и минут, минуты меньше 60). Предел
        /// ±14 часов здесь не проверяется — его проверяет <see cref="FromOffset"/>, чтобы сообщение было точным.
        /// Разбор вручную, без регулярного выражения: <c>\d</c> в .NET принимает и нелатинские цифры.
        /// </summary>
        public static bool TryParseOffset(string? text, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            bool shapeOk = text != null
                && text.Length == 6
                && (text[0] == '+' || text[0] == '-')
                && IsAsciiDigit(text[1])
                && IsAsciiDigit(text[2])
                && text[3] == ':'
                && IsAsciiDigit(text[4])
                && IsAsciiDigit(text[5]);
            if (!shapeOk)
            {
                return false;
            }

            int hours = ((text![1] - '0') * 10) + (text[2] - '0');
            int minutes = ((text[4] - '0') * 10) + (text[5] - '0');
            if (minutes >= 60)
            {
                return false;
            }

            var magnitude = new TimeSpan(hours, minutes, 0);
            offset = text[0] == '-' ? magnitude.Negate() : magnitude;
            return true;
        }

        private static bool IsAsciiDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        /// <summary>
        /// Границы периода из местных календарных дат (берётся только часть даты). Конец включителен по
        /// дню: период «с 10 по 10» охватывает весь день 10-го. Результат — полуинтервал
        /// [StartUtc, EndUtcExclusive).
        /// </summary>
        public (DateTime StartUtc, DateTime EndUtcExclusive) PeriodFromLocalDates(
            DateTime startLocalDate, DateTime endLocalDateInclusive)
        {
            RequireSupported(startLocalDate, nameof(startLocalDate));
            RequireSupported(endLocalDateInclusive, nameof(endLocalDateInclusive));
            DateTime startLocalMidnight = startLocalDate.Date;
            DateTime endLocalMidnightExclusive = endLocalDateInclusive.Date.AddDays(1);

            if (endLocalMidnightExclusive <= startLocalMidnight)
            {
                throw new ArgumentException("Конец периода раньше его начала.", nameof(endLocalDateInclusive));
            }

            DateTime startUtc = DateTime.SpecifyKind(startLocalMidnight - Offset, DateTimeKind.Utc);
            DateTime endUtc = DateTime.SpecifyKind(endLocalMidnightExclusive - Offset, DateTimeKind.Utc);
            return (startUtc, endUtc);
        }

        /// <summary>
        /// UTC → местное время приёмника (для предпросмотра и журнала). Результат — Unspecified. У крайних значений
        /// (<see cref="DateTime.MinValue"/>/<see cref="DateTime.MaxValue"/> — «нет даты» у битого элемента) сдвиг
        /// насыщается, а не бросает: показ времени не должен ронять предпросмотр или запись манифеста.
        /// </summary>
        public DateTime UtcToLocal(DateTime utc)
        {
            long ticks = TimeMapper.ToUtc(utc).Ticks + Offset.Ticks;
            if (ticks < DateTime.MinValue.Ticks)
            {
                ticks = DateTime.MinValue.Ticks;
            }
            else if (ticks > DateTime.MaxValue.Ticks)
            {
                ticks = DateTime.MaxValue.Ticks;
            }

            return new DateTime(ticks, DateTimeKind.Unspecified);
        }

        /// <summary>Первая дата периода, которую принимает программа (EWS работает с датами с 1601 года).</summary>
        public static readonly DateTime MinPeriodDate = new DateTime(1601, 1, 2);

        /// <summary>Последняя дата периода, которую принимает программа (предел дат EWS — 4500 год).</summary>
        public static readonly DateTime MaxPeriodDate = new DateTime(4500, 12, 30);

        private static void RequireSupported(DateTime date, string parameterName)
        {
            if (date.Date < MinPeriodDate || date.Date > MaxPeriodDate)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName, date, "Дата периода должна быть в пределах 1601–4500 годов.");
            }
        }
    }
}
