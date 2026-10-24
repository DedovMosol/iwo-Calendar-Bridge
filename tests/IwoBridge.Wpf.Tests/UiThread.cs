using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Один поток окон на все тесты разметки. В процессе может быть только одно приложение WPF, а его ресурсы (кисти,
    /// стили) принадлежат потоку, где оно создано, — поэтому все окна тестов строятся в этом же потоке.
    /// Приложение создаётся с настоящими ресурсами App.xaml, но без запуска (OnStartup не вызывается: ни окна
    /// «Подключение», ни сети) и без автоматического завершения при закрытии окон.
    /// </summary>
    internal static class UiThread
    {
        private static readonly Lazy<Dispatcher> Instance = new Lazy<Dispatcher>(Start, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Выполнить действие в потоке окон и дождаться его (исключения пробрасываются в тест).</summary>
        public static void Run(Func<Task> action)
        {
            Instance.Value.InvokeAsync(action).Task.Unwrap().GetAwaiter().GetResult();
        }

        public static void Run(Action action)
        {
            Instance.Value.InvokeAsync(action).Task.GetAwaiter().GetResult();
        }

        private static Dispatcher Start()
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            var thread = new Thread(() =>
            {
                try
                {
                    var app = new IwoBridge.Wpf.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    ready.SetResult(Dispatcher.CurrentDispatcher);
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ready.SetException(ex);
                    return;
                }

                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "UI tests",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // Поток фоновый и живёт до конца процесса тестов. Тесты выполняются в основном домене процесса
            // (xunit.runner.json: appDomain denied): выгрузка отдельного домена с работающим потоком окон роняет или
            // вешает процесс тестов.
            return ready.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>Сборщик ошибок привязок данных (опечатка в имени свойства в XAML видна только так).</summary>
    internal sealed class BindingErrors : TraceListener
    {
        private readonly StringBuilder _text = new StringBuilder();

        private BindingErrors()
        {
        }

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public static BindingErrors Attach()
        {
            var listener = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            return listener;
        }

        public override void Write(string? message)
        {
            lock (_text)
            {
                _text.Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            lock (_text)
            {
                _text.AppendLine(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>Поиск по дереву окна и геометрия элементов.</summary>
    internal static class Visuals
    {
        public static IEnumerable<T> All<T>(DependencyObject root)
            where T : DependencyObject
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject current = stack.Pop();
                if (current is T match)
                {
                    yield return match;
                }

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }

        public static T ByAutomationId<T>(DependencyObject root, string id)
            where T : FrameworkElement
        {
            return All<T>(root).Single(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id);
        }

        /// <summary>Прямоугольник элемента в координатах предка.</summary>
        public static Rect BoundsIn(FrameworkElement element, Visual ancestor)
        {
            return element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        }

        /// <summary>Дать окну отработать разметку и привязки.</summary>
        public static async Task Settle(Window window)
        {
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        /// <summary>Показать окно за пределами экрана, без фокуса и без значка на панели задач.</summary>
        public static void ShowOffscreen(Window window, double width, double height)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            if (!double.IsNaN(width))
            {
                window.Width = width;
            }

            if (!double.IsNaN(height))
            {
                window.Height = height;
            }

            window.Show();
        }
    }
}
