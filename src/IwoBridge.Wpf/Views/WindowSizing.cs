using System;
using System.Windows;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Окна не больше экрана. Размеры в XAML рассчитаны на 1920×1080; на 1366×768 при масштабе 125–150 % (рабочая
    /// область около 1090×580 единиц WPF) такое окно вылезло бы за край, и кнопки внизу стали бы недоступны.
    /// Поэтому при открытии каждое окно ужимается до 95 % рабочей области (без панели задач), а его минимальный
    /// размер — не больше её. Окна «по содержимому» (SizeToContent) получают предел высоты/ширины: лишнее
    /// прокручивается внутри. Предел обычному окну не ставится — иначе «Развернуть» не заполнило бы экран.
    /// Единицы WPF уже учитывают масштаб экрана, поэтому расчёт одинаков при любом DPI.
    /// </summary>
    internal static class WindowSizing
    {
        private const double ScreenShare = 0.95;

        public static void FitToWorkArea(Window window)
        {
            FitTo(window ?? throw new ArgumentNullException(nameof(window)), SystemParameters.WorkArea.Size);
        }

        /// <summary>Расчёт для заданной рабочей области (отдельно — для тестов).</summary>
        internal static void FitTo(Window window, Size workArea)
        {
            double maxWidth = Math.Floor(workArea.Width * ScreenShare);
            double maxHeight = Math.Floor(workArea.Height * ScreenShare);

            window.MinWidth = Math.Min(window.MinWidth, maxWidth);
            window.MinHeight = Math.Min(window.MinHeight, maxHeight);

            if (!double.IsNaN(window.Width))
            {
                window.Width = Math.Min(window.Width, maxWidth);
            }

            if (!double.IsNaN(window.Height))
            {
                window.Height = Math.Min(window.Height, maxHeight);
            }

            SizeToContent fit = window.SizeToContent;
            if (fit == SizeToContent.Height || fit == SizeToContent.WidthAndHeight)
            {
                window.MaxHeight = maxHeight;
            }

            if (fit == SizeToContent.Width || fit == SizeToContent.WidthAndHeight)
            {
                window.MaxWidth = maxWidth;
            }
        }
    }
}
