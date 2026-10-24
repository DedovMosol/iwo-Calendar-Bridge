using System;
using System.Windows;
using System.Windows.Controls;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Адаптивная страница внутри прокрутки: элементы сверху вниз, как StackPanel, но один из них (отмеченный
    /// <c>views:FillPanel.Fill="True"</c>) получает всё свободное место видимой области — и не меньше своей
    /// <c>MinHeight</c>.
    ///  - Большое окно: страница ровно по окну, прокрутки страницы нет, таблица растягивается и листается сама.
    ///  - Маленькое окно: заполняющий элемент сжимается до MinHeight, остальное не сжимается никогда, страница
    ///    прокручивается целиком — кнопки внизу всегда достижимы.
    /// Заполняющий элемент всегда получает конечную высоту: таблица внутри сохраняет виртуализацию строк (внутри
    /// обычной прокрутки она получила бы бесконечную высоту и построила бы тысячи строк разом).
    ///
    /// Почему не Grid с привязкой высоты: необходимую высоту остальных частей (сообщения, перенос строк на узком окне)
    /// заранее не знает никто — здесь она измеряется, а не угадывается порогом.
    /// <see cref="ViewportHeight"/> привязывается к <c>ScrollViewer.ViewportHeight</c> окружающей прокрутки; вне
    /// прокрутки используется доступная высота.
    /// </summary>
    public sealed class FillPanel : Panel
    {
        public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(
            nameof(ViewportHeight), typeof(double), typeof(FillPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
            "Fill", typeof(bool), typeof(FillPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        private double _fillHeight;

        /// <summary>Высота видимой области прокрутки (без отступов).</summary>
        public double ViewportHeight
        {
            get => (double)GetValue(ViewportHeightProperty);
            set => SetValue(ViewportHeightProperty, value);
        }

        public static bool GetFill(UIElement element)
        {
            return (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(FillProperty);
        }

        public static void SetFill(UIElement element, bool value)
        {
            (element ?? throw new ArgumentNullException(nameof(element))).SetValue(FillProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = availableSize.Width;
            double fixedHeight = 0;
            double desiredWidth = 0;
            UIElement? fill = null;

            foreach (UIElement child in InternalChildren)
            {
                if (child is null)
                {
                    continue;
                }

                if (fill is null && GetFill(child) && child.Visibility != Visibility.Collapsed)
                {
                    fill = child;
                    continue;
                }

                // Остальные части — по содержимому: таблиц в них нет, бесконечная высота безопасна.
                child.Measure(new Size(width, double.PositiveInfinity));
                fixedHeight += child.DesiredSize.Height;
                desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            }

            _fillHeight = 0;
            if (fill != null)
            {
                double space = double.IsInfinity(availableSize.Height) ? ViewportHeight : availableSize.Height;
                if (double.IsNaN(space) || double.IsInfinity(space))
                {
                    space = 0;
                }

                // MinHeight — без внешних отступов, а место выделяется вместе с ними.
                double minimum = fill is FrameworkElement element
                    ? element.MinHeight + element.Margin.Top + element.Margin.Bottom
                    : 0;
                _fillHeight = Math.Max(space - fixedHeight, minimum);
                fill.Measure(new Size(width, _fillHeight));
                desiredWidth = Math.Max(desiredWidth, fill.DesiredSize.Width);
            }

            return new Size(double.IsInfinity(width) ? desiredWidth : Math.Min(desiredWidth, width), fixedHeight + _fillHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double top = 0;
            bool fillPlaced = false;
            foreach (UIElement child in InternalChildren)
            {
                if (child is null)
                {
                    continue;
                }

                double height;
                if (!fillPlaced && GetFill(child) && child.Visibility != Visibility.Collapsed)
                {
                    fillPlaced = true;
                    height = _fillHeight;
                }
                else
                {
                    height = child.DesiredSize.Height;
                }

                child.Arrange(new Rect(0, top, finalSize.Width, height));
                top += height;
            }

            return finalSize;
        }
    }
}
