using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using IwoBridge.Wpf.Views;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Вкладка «Копирование» в настоящем окне за пределами экрана: нет ошибок привязок, кнопки предпросмотра и
    /// копирования помещаются и достижимы при любом размере окна, таблица действий виртуализирована и не меньше
    /// нескольких строк, в окне по умолчанию страница не прокручивается, Alt-буквы не повторяются; флажок строки
    /// в таблице действительно снимает действие. Демонстрационный режим — как при обычном запуске.
    /// </summary>
    public sealed class CopyUiTests
    {
        [Theory]
        [InlineData(720, 480)]
        [InlineData(900, 560)]
        [InlineData(1120, 780)]
        [InlineData(1600, 1000)]
        public void CopyTab_Preview_FitsReachableAndVirtualized(double width, double height)
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs();
                    MainViewModel vm = WithSource(3000, dialogs, out _);
                    var window = new MainWindow(vm, dialogs);
                    try
                    {
                        Visuals.ShowOffscreen(window, width, height);
                        await vm.Copy.PreviewCommand.ExecuteAsync();
                        await Visuals.Settle(window);

                        Assert.True(vm.Copy.HasPreview, vm.Copy.Error?.Title);
                        var root = (FrameworkElement)window.Content;
                        var page = Visuals.All<ScrollViewer>(window).Single(s => s.Name == "CopyScroll");
                        var content = Visuals.All<FillPanel>(window).Single(p => p.Name == "CopyContent");
                        foreach (string id in new[] { "CopyPreviewButton", "CopyButton" })
                        {
                            var button = Visuals.ByAutomationId<Button>(window, id);
                            Rect inWindow = Visuals.BoundsIn(button, root);
                            Assert.True(inWindow.Left >= -0.5 && inWindow.Right <= root.ActualWidth + 0.5, $"{id} по ширине: {inWindow}");
                            Rect inPage = Visuals.BoundsIn(button, content);
                            Assert.True(inPage.Bottom <= content.ActualHeight + 0.5, $"{id} недостижима прокруткой: {inPage}");
                            double needed = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
                            Assert.True(button.ActualWidth + 0.5 >= needed, $"{id} сжата: {button.ActualWidth} < {needed}");
                        }

                        var grid = Visuals.ByAutomationId<DataGrid>(window, "CopyRows");
                        Assert.True(grid.ActualHeight >= 120, $"таблица {grid.ActualHeight}");
                        Assert.InRange(Visuals.All<DataGridRow>(grid).Count(), 1, 200);
                        foreach (DatePickerTextBox box in Visuals.All<DatePickerTextBox>(window))
                        {
                            Assert.True(box.ExtentWidth <= box.ViewportWidth + 0.5, $"дата обрезана: «{box.Text}»");
                        }

                        if (height >= 780)
                        {
                            Assert.True(grid.ActualHeight >= 160, $"в большом окне таблица мала: {grid.ActualHeight}");
                            string parts = string.Join(", ", content.Children.OfType<FrameworkElement>().Select(c => c.ActualHeight.ToString("0")));
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
        public void CopyTab_Demo_DefaultSize_NoScroll_UniqueAccessKeys_RowCheckboxWorks()
        {
            UiThread.Run(async () =>
            {
                using (BindingErrors errors = BindingErrors.Attach())
                {
                    var dialogs = new FakeDialogs { AskAnswer = true };
                    var session = new DemoSession(DateTime.Today, TimeSpan.FromHours(3));
                    var vm = new MainViewModel(session, dialogs, new DemoSources());
                    var window = new MainWindow(vm, dialogs);
                    try
                    {
                        Visuals.ShowOffscreen(window, 1120, 780);
                        await Visuals.Settle(window);

                        // Список Outlook прочитан сразу при запуске, выбран первый календарь.
                        Assert.True(vm.Copy.UseOutlook);
                        Assert.Equal(DemoSources.Work.Label, vm.Copy.OutlookCalendar?.Label);
                        Assert.True(vm.PreviewCommand.CanExecute(null));

                        vm.PreviewCommand.Execute(null); // F5 на вкладке «Копирование»
                        await vm.Copy.Operation;
                        await Visuals.Settle(window);
                        Assert.True(vm.Copy.HasPreview, vm.Copy.Error?.Title);
                        Assert.False(vm.Cleanup.HasPreview);

                        var page = Visuals.All<ScrollViewer>(window).Single(s => s.Name == "CopyScroll");
                        Assert.True(page.ScrollableHeight == 0, $"прокрутка {page.ScrollableHeight:0}, видимая {page.ViewportHeight:0}, содержимое {page.ExtentHeight:0}");
                        AssertUniqueAccessKeys(window);

                        // Флажок первой строки снимается щелчком по самому флажку в таблице.
                        var grid = Visuals.ByAutomationId<DataGrid>(window, "CopyRows");
                        CheckBox first = Visuals.All<DataGridRow>(grid).OrderBy(r => r.GetIndex()).First()
                            .Let(r => Visuals.All<CheckBox>(r).Single());
                        first.IsChecked = false;
                        Assert.False(vm.Copy.Rows[0].IsSelected);

                        int total = vm.Copy.RowCount;
                        await vm.Copy.CopyCommand.ExecuteAsync();
                        await Visuals.Settle(window);
                        Assert.Equal("Готово", vm.Copy.ResultTitle);
                        Assert.Equal(total - 1, (await session.CopyTarget.ReadCopiesAsync(default)).Count);
                        var result = Visuals.ByAutomationId<TextBox>(window, "CopyResult");
                        Assert.True(result.IsVisible);
                        Assert.Equal(string.Empty, errors.Text);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }

        /// <summary>
        /// Кнопки внутри вкладок видны UI Automation (экранному диктору): шаблон вкладок обязан назвать область
        /// содержимого PART_SelectedContentHost — иначе содержимое вкладки для диктора пусто (найдено при съёмке экранов).
        /// </summary>
        [Fact]
        public void Tabs_ContentVisibleToAutomation()
        {
            foreach ((MainOperation operation, string id) in new[] { (MainOperation.Copy, "CopyPreviewButton"), (MainOperation.Cleanup, "PreviewButton") })
            {
                UiThread.Run(async () =>
                {
                    var dialogs = new FakeDialogs();
                    MainViewModel vm = WithSource(5, dialogs, out _);
                    vm.Operation = operation;
                    var window = new MainWindow(vm, dialogs);
                    try
                    {
                        Visuals.ShowOffscreen(window, 1120, 780);
                        await Visuals.Settle(window);
                        System.Windows.Automation.Peers.AutomationPeer root = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window);
                        Assert.Contains(id, AutomationIds(root));
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
        }

        private static System.Collections.Generic.List<string> AutomationIds(System.Windows.Automation.Peers.AutomationPeer peer)
        {
            var ids = new System.Collections.Generic.List<string>();
            var stack = new System.Collections.Generic.Stack<System.Windows.Automation.Peers.AutomationPeer>();
            stack.Push(peer);
            while (stack.Count > 0)
            {
                System.Windows.Automation.Peers.AutomationPeer current = stack.Pop();
                ids.Add(current.GetAutomationId());
                foreach (System.Windows.Automation.Peers.AutomationPeer child in current.GetChildren() ?? new System.Collections.Generic.List<System.Windows.Automation.Peers.AutomationPeer>())
                {
                    stack.Push(child);
                }
            }

            return ids;
        }

        [Fact]
        public void Tabs_SwitchOperation_BlockedWhileBusy()
        {
            UiThread.Run(async () =>
            {
                var dialogs = new FakeDialogs();
                MainViewModel vm = WithSource(5, dialogs, out _);
                var window = new MainWindow(vm, dialogs);
                try
                {
                    Visuals.ShowOffscreen(window, 1120, 780);
                    await Visuals.Settle(window);
                    Assert.NotNull(Visuals.ByAutomationId<Button>(window, "CopyPreviewButton"));

                    vm.Operation = MainOperation.Cleanup;
                    await Visuals.Settle(window);
                    Assert.NotNull(Visuals.ByAutomationId<Button>(window, "PreviewButton"));
                    Assert.DoesNotContain(Visuals.All<Button>(window), b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "CopyPreviewButton");
                    AssertUniqueAccessKeys(window);
                    Assert.Equal(2, vm.CleanupChoices.Count); // приёмник и источник по EWS

                    vm.Operation = MainOperation.Copy;
                    await Visuals.Settle(window);
                    AssertUniqueAccessKeys(window);

                    // Во время чтения вкладка не переключается, а Esc (StopCommand окна) останавливает операцию.
                    var gate = new CopyViewModelTests.GateSource();
                    vm.Copy.SetEwsSource(new CopyViewModelTests.GateSession(gate));
                    System.Threading.Tasks.Task preview = vm.Copy.PreviewCommand.ExecuteAsync();
                    await gate.Entered.Task;
                    vm.OperationIndex = 1;
                    await Visuals.Settle(window);
                    Assert.Equal(MainOperation.Copy, vm.Operation);
                    Assert.Equal(0, Visuals.All<TabControl>(window).Single(t => ReferenceEquals(t.DataContext, vm)).SelectedIndex);
                    Assert.False(vm.EditConnectionCommand.CanExecute(null));
                    Assert.True(vm.StopCommand.CanExecute(null));
                    vm.StopCommand.Execute(null);
                    await preview;
                    Assert.False(vm.IsBusy);
                    Assert.True(vm.EditConnectionCommand.CanExecute(null));
                }
                finally
                {
                    window.Close();
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

        /// <summary>Главное окно с приёмником и источником по EWS на <paramref name="count"/> событий.</summary>
        private static MainViewModel WithSource(int count, FakeDialogs dialogs, out TestSession target)
        {
            DateTime start = DateTime.UtcNow.Date.AddDays(-3).AddHours(9);
            target = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));
            var source = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()))
            {
                TargetLabel = CopyViewModelTests.SourceMailbox,
                SourceCalendar = new InMemorySource("источник", Enumerable.Range(0, count)
                    .Select(i => new SourceItem("s" + i, "Очень длинная тема совещания, которая не помещается в колонку " + i, null, null,
                        start.AddMinutes(i), start.AddMinutes(i + 30)))
                    .ToArray()),
            };
            var settings = new MemorySettings
            {
                Profile = new IwoBridge.Core.Configuration.ConnectionProfile(new Uri("https://mail.example.test/EWS/Exchange.asmx"), null, TimeSpan.FromHours(3), null),
                Credentials = new IwoBridge.Core.Security.CredentialSecret("EXAMPLE\\user", "x"),
            };
            var sourceSettings = new MemorySettings { Profile = settings.Profile, Credentials = settings.Credentials };
            var sessions = new System.Collections.Generic.Queue<ITargetSession>(new ITargetSession[] { source, target });
            var connector = new QueueConnector(sessions);
            var vm = new MainViewModel(settings, connector, dialogs, dialogs, () => TimeSpan.FromHours(3), sourceSettings, null);
            vm.Start();
            vm.Copy.UseEws = true;
            return vm;
        }

        /// <summary>Отдаёт заранее заданные сеансы по порядку открытия (источник, затем приёмник).</summary>
        private sealed class QueueConnector : ITargetConnector
        {
            private readonly System.Collections.Generic.Queue<ITargetSession> _sessions;

            public QueueConnector(System.Collections.Generic.Queue<ITargetSession> sessions) => _sessions = sessions;

            public System.Threading.Tasks.Task<ConnectionCheckResult> CheckAsync(
                IwoBridge.Core.Configuration.ConnectionProfile profile, IwoBridge.Core.Security.CredentialSecret credentials, System.Threading.CancellationToken cancellationToken) =>
                System.Threading.Tasks.Task.FromResult(ConnectionCheckResult.Success(0, null, null, null));

            public ITargetSession Open(IwoBridge.Core.Configuration.ConnectionProfile profile, IwoBridge.Core.Security.CredentialSecret credentials) => _sessions.Dequeue();
        }
    }

    internal static class Functional
    {
        public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
    }
}
