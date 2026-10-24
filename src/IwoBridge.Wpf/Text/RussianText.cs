using System;
using System.Globalization;
using IwoBridge.Core.Time;

namespace IwoBridge.Wpf.Text
{
    /// <summary>
    /// Оформление чисел, дат и склонений для окна — одно место (DRY). Числа — с разделением разрядов
    /// («12 345»), даты — «дд.ММ.гггг чч:мм» в поясе приёмника, существительные — в правильной форме после числа.
    /// </summary>
    public static class RussianText
    {
        public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>«1 событие», «2 события», «5 событий», «11 событий», «21 событие».</summary>
        public static string Count(int count, string one, string few, string many)
        {
            return count.ToString("N0", Culture) + " " + Plural(count, one, few, many);
        }

        /// <summary>Форма существительного после числа по правилам русского языка.</summary>
        public static string Plural(int count, string one, string few, string many)
        {
            int n = Math.Abs(count) % 100;
            if (n >= 11 && n <= 14)
            {
                return many;
            }

            switch (n % 10)
            {
                case 1:
                    return one;
                case 2:
                case 3:
                case 4:
                    return few;
                default:
                    return many;
            }
        }

        public static string Number(int value)
        {
            return value.ToString("N0", Culture);
        }

        /// <summary>Момент UTC → «дд.ММ.гггг чч:мм» в поясе приёмника.</summary>
        public static string Moment(DateTime utc, TargetTimeZone zone)
        {
            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            return zone.UtcToLocal(utc).ToString("dd.MM.yyyy HH:mm", Culture);
        }

        /// <summary>Дата без времени: «дд.ММ.гггг».</summary>
        public static string Date(DateTime date)
        {
            return date.ToString("dd.MM.yyyy", Culture);
        }

        /// <summary>Пояс для подписи: «UTC+03:00».</summary>
        public static string Zone(TargetTimeZone zone)
        {
            if (zone is null)
            {
                throw new ArgumentNullException(nameof(zone));
            }

            return "UTC" + zone.OffsetText;
        }
    }
}
