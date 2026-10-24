using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IwoBridge.InMemory;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;
using Xunit;
using MainOperation = IwoBridge.Wpf.ViewModels.MainOperation;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Настоящие окна с настоящими ресурсами App.xaml, построенные за пределами экрана. Проверяется то, что
    /// модели представления проверить не могут:
    ///  - нет ошибок привязок (опечатка в имени свойства в XAML молча оставила бы поле пустым);
    ///  - при любом размере окна от минимального до большого кнопки помещаются по ширине и достижимы, даты в полях
    ///    не обрезаны, таблица предпросмотра сохраняет виртуализацию (строится лишь видимая часть из 3 000 строк);
    ///  - в большом окне страница не прокручивается — таблица занимает свободное место;
    ///  - буквы Alt-сокращений не повторяются, F5 и Esc связаны с командами;
    ///  - окна «Подключение» и «Подтвердите удаление» на маленьком экране не теряют кнопок;
    ///  - каждая кисть палитры имеет замену для высокой контрастности.
    /// </summary>
    public sealed class UiLayoutTests
    {
        private const string LongSubject =
            "Очень длинная тема совещания, которая не помещается в колонку и должна обрезаться многоточием — ";

        [Theory]
        [InlineData(720, 480)]
        [InlineData(900, 560)]
        [InlineData(1120, 780)]
        [InlineData(1600, 1000)]
        public void MainWindow_Preview_FitsReachableAndVirtualized(double width, double height)
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs();
                    var vm = new MainViewModel(Flood(3000), dialogs);
                    vm.Operation = MainOperation.Cleanup; // вкладка «Очистка» (по умолчанию открыто «Копирование»)
                var window = new MainWindow(vm, dialogs);
                    try
                    {
                        Visuals.ShowOffscreen(window, width, height);
                        vm.Cleanup.IsWholeCalendar = true;
                        await vm.Cleanup.PreviewCommand.ExecuteAsync();
                        await Visuals.Settle(window);

                        Assert.True(vm.Cleanup.HasPreview, vm.Cleanup.Error?.Title);
                        var root = (FrameworkElement)window.Content;
                        var page = (ScrollViewer)window.FindName("Page");
                        var content = (FrameworkElement)window.FindName("PageContent");

                        foreach (string id in new[] { "PreviewButton", "DeleteButton" })
                        {
                            var button = Visuals.ByAutomationId<Button>(window, id);
                            Rect inWindow = Visuals.BoundsIn(button, root);
                            Assert.True(inWindow.Left >= -0.5 && inWindow.Right <= root.ActualWidth + 0.5, $"{id} по ширине: {inWindow}");
                            Rect inPage = Visuals.BoundsIn(button, content);
                            Assert.True(inPage.Bottom <= content.ActualHeight + 0.5, $"{id} недостижима прокруткой: {inPage}");
                            // Желаемый размер — вместе с внешними отступами; кнопка не сжата уже своего текста.
                            double needed = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
                            Assert.True(button.ActualWidth + 0.5 >= needed, $"{id} сжата: {button.ActualWidth} < {needed}");
                        }

                        DataGrid grid = Visuals.All<DataGrid>(window).Single(g => g.IsVisible);
                        // Даже в минимальном окне: заголовок колонок и не меньше трёх строк по 28 единиц.
                        Assert.True(grid.ActualHeight >= 120, $"таблица {grid.ActualHeight}");
                        if (height >= 780)
                        {
                            Assert.True(grid.ActualHeight >= 160, $"в большом окне таблица мала: {grid.ActualHeight}");
                        }
                        int realized = Visuals.All<DataGridRow>(grid).Count();
                        Assert.InRange(realized, 1, 200);

                        foreach (DatePickerTextBox box in Visuals.All<DatePickerTextBox>(window))
                        {
                            Assert.True(box.ExtentWidth <= box.ViewportWidth + 0.5, $"дата обрезана: «{box.Text}»");
                        }

                        if (height >= 780)
                        {
                            string parts = string.Join(", ", ((Panel)content).Children.OfType<FrameworkElement>().Select(c => c.ActualHeight.ToString("0")));
                            Assert.True(page.ScrollableHeight == 0, $"страница прокручивается на {page.ScrollableHeight:0} при видимой высоте {page.ViewportHeight:0}; части: {parts}");
                        }

                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void MainWindow_DemoAtDefaultSize_NoPageScroll()
        {
            // Как при обычном запуске: демонстрационный календарь, период «последний месяц», серии на границе.
            UiThread.Run(async () =>
            {
                var dialogs = new FakeDialogs();
                var vm = new MainViewModel(new IwoBridge.Wpf.Services.DemoSession(DateTime.Today, TimeSpan.FromHours(3)), dialogs);
                vm.Operation = MainOperation.Cleanup; // вкладка «Очистка» (по умолчанию открыто «Копирование»)
                var window = new MainWindow(vm, dialogs);
                try
                {
                    Visuals.ShowOffscreen(window, 1120, 780);
                    await vm.Cleanup.PreviewCommand.ExecuteAsync();
                    await Visuals.Settle(window);

                    Assert.True(vm.Cleanup.HasPreview);
                    var page = (ScrollViewer)window.FindName("Page");
                    var content = (Panel)window.FindName("PageContent");
                    string parts = string.Join(", ", content.Children.OfType<FrameworkElement>()
                        .Select(c => c.ActualHeight.ToString("0.##") + "/" + c.DesiredSize.Height.ToString("0.##")));
                    Assert.True(page.ScrollableHeight == 0,
                        $"прокрутка {page.ScrollableHeight:0.##}, видимая {page.ViewportHeight:0.##}, содержимое {page.ExtentHeight:0.##}; части {parts}");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void MainWindow_SmallWindow_PageScrolls_ListKeepsMinimum()
        {
            UiThread.Run(async () =>
            {
                var dialogs = new FakeDialogs();
                var vm = new MainViewModel(Flood(50), dialogs);
                vm.Operation = MainOperation.Cleanup; // вкладка «Очистка» (по умолчанию открыто «Копирование»)
                var window = new MainWindow(vm, dialogs);
                try
                {
                    Visuals.ShowOffscreen(window, 720, 480);
                    vm.Cleanup.IsWholeCalendar = true;
                    await vm.Cleanup.PreviewCommand.ExecuteAsync();
                    await Visuals.Settle(window);

                    var page = (ScrollViewer)window.FindName("Page");
                    Assert.True(page.ScrollableHeight > 0);
                    var tabs = (TabControl)window.FindName("CleanupLists");
                    // Не меньше вкладок, заголовка колонок и трёх строк по 28 единиц.
                    Assert.True(tabs.ActualHeight >= 140, $"список {tabs.ActualHeight}");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void MainWindow_AccessKeysUnique_HotkeysBound()
        {
            UiThread.Run(async () =>
            {
                var dialogs = new FakeDialogs();
                var vm = new MainViewModel(Flood(5), dialogs);
                vm.Operation = MainOperation.Cleanup; // вкладка «Очистка» (по умолчанию открыто «Копирование»)
                var window = new MainWindow(vm, dialogs);
                try
                {
                    Visuals.ShowOffscreen(window, 1120, 780);
                    await vm.Cleanup.PreviewCommand.ExecuteAsync();
                    await Visuals.Settle(window);

                    AssertUniqueAccessKeys(window);
                    KeyBinding[] keys = window.InputBindings.OfType<KeyBinding>().ToArray();
                    // Клавиши окна — для открытой вкладки: F5 строит предпросмотр её операции, Esc останавливает идущую.
                    Assert.Same(vm.PreviewCommand, keys.Single(k => k.Key == Key.F5).Command);
                    Assert.Same(vm.StopCommand, keys.Single(k => k.Key == Key.Escape).Command);

                    // Enter строит предпросмотр, только пока его нет: с готовым списком Enter ничего не перестраивает.
                    var preview = Visuals.ByAutomationId<Button>(window, "PreviewButton");
                    Assert.False(preview.IsDefault);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void ConnectionWindow_SmallScreen_ButtonsStayVisible_NoBindingErrors()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs();
                    var vm = new ConnectionViewModel(
                        new MemorySettings(), new FakeConnector(), dialogs, TimeSpan.FromHours(3), null, null, "Файл настроек повреждён.");
                    var window = new ConnectionWindow(vm) { MaxHeight = 380 };
                    try
                    {
                        Visuals.ShowOffscreen(window, double.NaN, double.NaN);
                        await Visuals.Settle(window);

                        var root = (FrameworkElement)window.Content;
                        foreach (Button button in Visuals.All<Button>(window).Where(b => b.IsVisible && b.Content is string text && text.Length > 0))
                        {
                            if (button.Content as string == "Забыть сертификат")
                            {
                                continue;
                            }

                            Rect bounds = Visuals.BoundsIn(button, root);
                            bool insideScroll = Visuals.All<ScrollViewer>(window).Any(s => s.IsAncestorOf(button) && s.Name != "PART_ContentHost");
                            if (!insideScroll)
                            {
                                Assert.True(bounds.Bottom <= root.ActualHeight + 0.5, $"«{button.Content}» за краем окна: {bounds}");
                            }
                        }

                        Assert.True(Visuals.All<ScrollViewer>(window).Any(s => s.ScrollableHeight > 0), "лишнее должно прокручиваться");
                        AssertUniqueAccessKeys(window);
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void ConfirmWindow_MatchShown_HardDeleteNeedsTick_NoBindingErrors()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var vm = new DeletionConfirmationViewModel("ящик", "user@example.test", "3 события", hardDelete: true);
                    var window = new ConfirmDeletionWindow(vm);
                    try
                    {
                        Visuals.ShowOffscreen(window, double.NaN, double.NaN);
                        await Visuals.Settle(window);
                        TextBlock match = Visuals.All<TextBlock>(window).Single(t => t.Text == "Адрес совпадает");
                        var confirm = Visuals.ByAutomationId<Button>(window, "ConfirmDeleteButton");
                        Assert.False(match.IsVisible);
                        Assert.False(confirm.IsEnabled);

                        vm.TypedPhrase = " USER@example.test ";
                        await Visuals.Settle(window);
                        Assert.True(match.IsVisible);
                        Assert.False(confirm.IsEnabled);

                        vm.UnderstoodIrreversible = true;
                        await Visuals.Settle(window);
                        Assert.True(confirm.IsEnabled);

                        Button cancel = Visuals.All<Button>(window).Single(b => b.IsCancel);
                        Assert.True(cancel.IsDefault);
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void QuestionWindow_ButtonsApart_CancelIsDefault_ConfirmReturnsTrue()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var window = new QuestionWindow("Скопировать?", "Откуда: А\nКуда: Б\n\n• создать 3 события", "Скопировать", "Отмена");
                    try
                    {
                        Visuals.ShowOffscreen(window, double.NaN, double.NaN);
                        await Visuals.Settle(window);
                        var root = (FrameworkElement)window.Content;
                        var cancel = Visuals.ByAutomationId<Button>(window, "CancelButton");
                        var confirm = Visuals.ByAutomationId<Button>(window, "ConfirmButton");

                        // Отказ — у левого края, действие — у правого: между ними пусто, промахнуться трудно.
                        Rect left = Visuals.BoundsIn(cancel, root), right = Visuals.BoundsIn(confirm, root);
                        Assert.True(left.Left < 30, $"«Отмена» не слева: {left}");
                        Assert.True(right.Right > root.ActualWidth - 30, $"«Скопировать» не справа: {right}");
                        Assert.True(right.Left - left.Right > 100, "кнопки рядом");
                        Assert.True(cancel.IsDefault && cancel.IsCancel, "Enter и Esc должны отказывать");
                        Assert.Equal("Скопировать", confirm.Content);
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void ConfirmWindow_TinyScreen_ButtonsStayInside()
        {
            UiThread.Run(async () =>
            {
                var vm = new DeletionConfirmationViewModel("ящик", "user@example.test", "3 события", hardDelete: true);
                var window = new ConfirmDeletionWindow(vm) { MaxHeight = 300 };
                try
                {
                    Visuals.ShowOffscreen(window, double.NaN, double.NaN);
                    await Visuals.Settle(window);
                    var root = (FrameworkElement)window.Content;
                    var confirm = Visuals.ByAutomationId<Button>(window, "ConfirmDeleteButton");
                    Assert.True(Visuals.BoundsIn(confirm, root).Bottom <= root.ActualHeight + 0.5);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void Palette_EveryBrushHasHighContrastReplacement()
        {
            UiThread.Run(() =>
            {
                ResourceDictionary resources = Application.Current.Resources;
                string[] brushes = resources.Keys.OfType<string>().Where(k => resources[k] is Brush).OrderBy(k => k, StringComparer.Ordinal).ToArray();

                Assert.Equal(Theme.HighContrastPalette.Keys.OrderBy(k => k, StringComparer.Ordinal), brushes);
            });
        }

        [Fact]
        public void Theme_SwitchesToSystemColors_AndBack()
        {
            UiThread.Run(() =>
            {
                var resources = new ResourceDictionary();
                foreach (string key in Theme.HighContrastPalette.Keys)
                {
                    resources[key] = new SolidColorBrush(Color.FromRgb(1, 2, 3));
                }

                IReadOnlyDictionary<string, object> original = Theme.Snapshot(resources);
                Theme.Apply(resources, original, highContrast: true);
                Assert.Same(SystemColors.WindowTextBrush, resources["TextBrush"]);
                Assert.Same(SystemColors.HighlightBrush, resources["AccentBrush"]);

                Theme.Apply(resources, original, highContrast: false);
                foreach (string key in Theme.HighContrastPalette.Keys)
                {
                    Assert.Same(original[key], resources[key]);
                }

                resources.Remove("RowAltBrush");
                Assert.Throws<InvalidOperationException>(() => Theme.Snapshot(resources));
            });
        }

        [Fact]
        public void FillPanel_FillsViewport_ButNotBelowMinimum()
        {
            UiThread.Run(() =>
            {
                var top = new Border { Height = 100 };
                var fill = new Border { MinHeight = 50, Margin = new Thickness(0, 0, 0, 10) };
                FillPanel.SetFill(fill, true);
                var bottom = new Border { Height = 30 };
                var panel = new FillPanel { ViewportHeight = 400 };
                panel.Children.Add(top);
                panel.Children.Add(fill);
                panel.Children.Add(bottom);

                panel.Measure(new Size(300, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.Equal(400, panel.DesiredSize.Height);
                Assert.Equal(260, fill.ActualHeight); // 400 − 100 − 30 − отступ 10

                panel.ViewportHeight = 120; // окно меньше содержимого: заполняющий — до минимума, страница прокручивается
                panel.Measure(new Size(300, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.Equal(190, panel.DesiredSize.Height); // 100 + 50 + 10 + 30
                Assert.Equal(50, fill.ActualHeight);
                Assert.Equal(160, Visuals.BoundsIn(bottom, panel).Top);

                fill.Visibility = Visibility.Collapsed; // предпросмотра нет — остальное просто друг под другом
                panel.Measure(new Size(300, double.PositiveInfinity));
                Assert.Equal(130, panel.DesiredSize.Height);
            });
        }

        [Fact]
        public void WindowSizing_ClampsToWorkArea_ButKeepsMaximizeFree()
        {
            UiThread.Run(() =>
            {
                var main = new Window { Width = 1120, Height = 780, MinWidth = 720, MinHeight = 480 };
                WindowSizing.FitTo(main, new Size(800, 500));
                Assert.Equal(760, main.Width);
                Assert.Equal(475, main.Height);
                Assert.Equal(720, main.MinWidth);
                Assert.Equal(475, main.MinHeight);
                Assert.True(double.IsPositiveInfinity(main.MaxHeight));

                var dialog = new Window { Width = 680, SizeToContent = SizeToContent.Height, MinHeight = 360 };
                WindowSizing.FitTo(dialog, new Size(1366, 728));
                Assert.Equal(680, dialog.Width);
                Assert.Equal(691, dialog.MaxHeight);
            });
        }

        [Fact]
        public void Announce_SetsText()
        {
            UiThread.Run(() =>
            {
                var block = new TextBlock();
                Announce.SetText(block, "Готово");
                Assert.Equal("Готово", block.Text);
                Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(block));
                Announce.SetText(block, null);
                Assert.Equal(string.Empty, block.Text);
            });
        }

        [Fact]
        public void BindingErrors_CollectorCatchesTypo()
        {
            // Проверка самой проверки: без этого «нет ошибок привязок» могло бы означать «сборщик не подключён».
            UiThread.Run(() =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var block = new TextBlock { DataContext = new DeletionConfirmationViewModel("ящик", "a@b.test", "1", false) };
                    block.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Sumary"));
                    Assert.Contains("Sumary", errors.Text);
                }
            });
        }

        private static void AssertUniqueAccessKeys(Window window)
        {
            string[] keys = Visuals.All<AccessText>(window)
                .Where(t => t.IsVisible && t.AccessKey != default(char))
                .Select(t => char.ToLowerInvariant(t.AccessKey).ToString())
                .ToArray();
            string[] repeated = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(repeated.Length == 0, "повторяются Alt-буквы: " + string.Join(", ", repeated));
        }

        private static TestSession Flood(int count)
        {
            DateTime start = DateTime.UtcNow.Date.AddDays(-3).AddHours(9);
            return new TestSession(new InMemoryCalendar(Enumerable.Range(0, count)
                .Select(i => InMemoryEvent.Single("e" + i, start.AddMinutes(i), start.AddMinutes(i + 30), subject: LongSubject + i))
                .ToArray()));
        }
    }
}
