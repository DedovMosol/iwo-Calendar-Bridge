using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Режим высокой контрастности Windows. Все цвета окон подключены через DynamicResource по именам из App.xaml
    /// (TextBrush, AccentBrush…). В обычном режиме действуют цвета из App.xaml; при включённой высокой контрастности
    /// они заменяются системными (SystemColors) — окна перекрашиваются на лету, без перезапуска, и при включении, и
    /// при выключении, и при смене одной контрастной темы на другую.
    ///
    /// Почему подмена, а не просто системные цвета везде: системная палитра обычного режима бледная (серые рамки,
    /// синий выделения), а собственная — с выверенным контрастом. В контрастном режиме наоборот: пользователь выбрал
    /// цвета сам, и программа обязана их соблюдать.
    /// </summary>
    internal static class Theme
    {
        /// <summary>
        /// Какой системный цвет заменяет каждый цвет палитры в контрастном режиме. Ключи — ровно все кисти App.xaml
        /// (проверяет тест): забытая кисть осталась бы «своей» и могла бы стать нечитаемой на чёрном фоне.
        /// Смысловые цвета (ошибка, успех, предупреждение) в контрастном режиме не различаются фоном — смысл передают
        /// заголовки и тексты сообщений, а рамки остаются видимыми (цвет текста).
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, Func<Brush>> HighContrastPalette =
            new Dictionary<string, Func<Brush>>(StringComparer.Ordinal)
            {
                ["TextBrush"] = () => SystemColors.WindowTextBrush,
                ["AccentBrush"] = () => SystemColors.HighlightBrush,
                ["AccentTextBrush"] = () => SystemColors.HighlightTextBrush,
                ["DangerBrush"] = () => SystemColors.HighlightBrush,
                ["DisabledBackBrush"] = () => SystemColors.ControlBrush,
                ["DisabledTextBrush"] = () => SystemColors.GrayTextBrush,
                ["MutedTextBrush"] = () => SystemColors.WindowTextBrush,
                ["SurfaceBrush"] = () => SystemColors.WindowBrush,
                ["PageBrush"] = () => SystemColors.WindowBrush,
                ["HeaderBrush"] = () => SystemColors.WindowBrush,
                ["LineBrush"] = () => SystemColors.WindowTextBrush,
                ["InputBorderBrush"] = () => SystemColors.WindowTextBrush,
                ["FocusBrush"] = () => SystemColors.WindowTextBrush,
                ["HoverBackBrush"] = () => SystemColors.ControlBrush,
                ["ErrorTextBrush"] = () => SystemColors.WindowTextBrush,
                ["ErrorBackBrush"] = () => SystemColors.WindowBrush,
                ["ErrorLineBrush"] = () => SystemColors.WindowTextBrush,
                ["SuccessTextBrush"] = () => SystemColors.WindowTextBrush,
                ["SuccessBackBrush"] = () => SystemColors.WindowBrush,
                ["SuccessLineBrush"] = () => SystemColors.WindowTextBrush,
                ["InfoBackBrush"] = () => SystemColors.WindowBrush,
                ["InfoLineBrush"] = () => SystemColors.WindowTextBrush,
                ["WarningBackBrush"] = () => SystemColors.WindowBrush,
                ["WarningLineBrush"] = () => SystemColors.WindowTextBrush,
                ["GridLineBrush"] = () => SystemColors.WindowTextBrush,
                ["RowAltBrush"] = () => SystemColors.WindowBrush,
                ["ConsoleBackBrush"] = () => SystemColors.WindowBrush,
                ["ConsoleTextBrush"] = () => SystemColors.WindowTextBrush,
                ["ConsoleSuccessBrush"] = () => SystemColors.WindowTextBrush,
                ["ConsoleProblemBrush"] = () => SystemColors.WindowTextBrush,
                ["ConsoleMutedBrush"] = () => SystemColors.WindowTextBrush,
            };

        /// <summary>
        /// Применять тему к ресурсам приложения сейчас и при каждой смене системных настроек. Вызывать один раз при
        /// запуске из потока окон; результат освободить при выходе (подписка на статические события Windows иначе
        /// удерживает обработчик до конца процесса).
        /// </summary>
        public static IDisposable Attach(ResourceDictionary resources, Dispatcher dispatcher)
        {
            if (resources is null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            if (dispatcher is null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            IReadOnlyDictionary<string, object> original = Snapshot(resources);
            Apply(resources, original, SystemParameters.HighContrast);
            return new Subscription(resources, original, dispatcher);
        }

        /// <summary>Исходные (обычные) кисти палитры. Нет нужной кисти в ресурсах — ошибка сборки окон, а не тихий пропуск.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(ResourceDictionary resources)
        {
            var original = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in HighContrastPalette.Keys)
            {
                if (!resources.Contains(key))
                {
                    throw new InvalidOperationException($"В ресурсах приложения нет кисти «{key}».");
                }

                original[key] = resources[key];
            }

            return original;
        }

        /// <summary>
        /// Поставить контрастные или исходные кисти. Неизменившиеся значения не переписываются: каждая запись в
        /// ресурсы заставляет перечитать её все элементы со ссылкой DynamicResource.
        /// </summary>
        internal static void Apply(ResourceDictionary resources, IReadOnlyDictionary<string, object> original, bool highContrast)
        {
            foreach (KeyValuePair<string, Func<Brush>> entry in HighContrastPalette)
            {
                object wanted = highContrast ? entry.Value() : original[entry.Key];
                if (!SameBrush(resources[entry.Key], wanted))
                {
                    resources[entry.Key] = wanted;
                }
            }
        }

        private static bool SameBrush(object current, object wanted)
        {
            if (ReferenceEquals(current, wanted))
            {
                return true;
            }

            return current is SolidColorBrush a && wanted is SolidColorBrush b && a.Color == b.Color && a.Opacity.Equals(b.Opacity);
        }

        /// <summary>
        /// Слежение за настройками. Два источника, потому что ни один не покрывает все случаи:
        ///  - <see cref="SystemParameters.StaticPropertyChanged"/> — включение и выключение высокой контрастности;
        ///  - <see cref="SystemEvents.UserPreferenceChanged"/> (цвета) — смена одной контрастной темы на другую,
        ///    когда флаг контрастности не меняется, а системные цвета — да.
        /// Применение откладывается в очередь окна с низким приоритетом: к этому моменту WPF уже обновил свои
        /// SystemColors и SystemParameters. Повторные события за один раз сливаются в одно применение.
        /// </summary>
        private sealed class Subscription : IDisposable
        {
            private readonly ResourceDictionary _resources;
            private readonly IReadOnlyDictionary<string, object> _original;
            private readonly Dispatcher _dispatcher;
            private readonly object _gate = new object();
            private bool _pending;
            private bool _disposed;

            public Subscription(ResourceDictionary resources, IReadOnlyDictionary<string, object> original, Dispatcher dispatcher)
            {
                _resources = resources;
                _original = original;
                _dispatcher = dispatcher;
                SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                }

                SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }

            private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(SystemParameters.HighContrast))
                {
                    Schedule();
                }
            }

            private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
            {
                if (e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.Accessibility)
                {
                    Schedule();
                }
            }

            // События Windows могут прийти из другого потока — сюда допускается только постановка в очередь окна.
            private void Schedule()
            {
                lock (_gate)
                {
                    if (_pending || _disposed)
                    {
                        return;
                    }

                    _pending = true;
                }

                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    lock (_gate)
                    {
                        _pending = false;
                        if (_disposed)
                        {
                            return;
                        }
                    }

                    Apply(_resources, _original, SystemParameters.HighContrast);
                }));
            }
        }
    }
}
