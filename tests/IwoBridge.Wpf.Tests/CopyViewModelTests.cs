using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Экран копирования по шагам: источник (Outlook или EWS) → предпросмотр с флажками → подтверждение → запись →
    /// итог; правила: выполняется только отмеченное и только по актуальному предпросмотру, массовое удаление копий —
    /// после явного разрешения, сбой одного события не останавливает остальные, сбой подключения — останавливает.
    /// Пояс приёмника — UTC+03:00, период — январь 2026.
    /// </summary>
    public sealed class CopyViewModelTests
    {
        private static DateTime Utc(int day, int hour) => new DateTime(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        private static SourceItem Item(string id, int day, string subject = "Встреча") =>
            new SourceItem(id, subject, "Зал", "Повестка", Utc(day, 9), Utc(day, 10));

        private static (CopyViewModel Vm, TestSession Target, FakeDialogs Dialogs) Create(
            IOutlookCalendars? outlook = null, params SourceItem[] ewsItems)
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            var target = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));
            var source = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()))
            {
                SourceCalendar = new InMemorySource("источник по EWS", ewsItems),
            };
            var vm = new CopyViewModel(dialogs, outlook, () => new DateTime(2026, 1, 20));
            vm.SetTarget(target);
            vm.SetEwsSource(source);
            vm.UseEws = true;
            vm.PeriodStart = new DateTime(2026, 1, 1);
            vm.PeriodEnd = new DateTime(2026, 1, 31);
            return (vm, target, dialogs);
        }

        [Fact]
        public async Task Preview_FromEws_ListsCreates_AllSelected()
        {
            (CopyViewModel vm, TestSession target, _) = Create(null, Item("a", 10), Item("b", 11), Item("c", 12));

            await vm.PreviewCommand.ExecuteAsync();

            Assert.True(vm.HasPreview, vm.Error?.Title);
            Assert.Equal(3, vm.RowCount);
            Assert.All(vm.Rows, r => Assert.True(r.IsSelected));
            Assert.All(vm.Rows, r => Assert.Equal("Создать", r.Kind));
            Assert.Contains(vm.Rows, r => r.Start == "10.01.2026 12:00");
            Assert.Equal("создать 3", vm.Summary);
            Assert.True(vm.CopyCommand.CanExecute(null));
            Assert.Empty(target.Copies.Copies); // предпросмотр ничего не пишет
        }

        [Fact]
        public async Task Copy_WritesOnlySelectedRows_ThenNextPreviewHasNothingToDo()
        {
            (CopyViewModel vm, TestSession target, FakeDialogs dialogs) = Create(null, Item("a", 10, "A"), Item("b", 11, "B"), Item("c", 12, "C"));
            await vm.PreviewCommand.ExecuteAsync();
            vm.Rows.Single(r => r.Subject == "B").IsSelected = false;

            await vm.CopyCommand.ExecuteAsync();

            Assert.Equal(new[] { "A", "C" }, target.Copies.Copies.Select(c => c.Item.Subject).OrderBy(s => s));
            Assert.Contains("Создать: 2", dialogs.Questions.Single());
            Assert.Equal("Готово", vm.ResultTitle);
            Assert.True(vm.ResultIsSuccess);
            Assert.False(vm.HasPreview); // предпросмотр израсходован

            await vm.PreviewCommand.ExecuteAsync();
            Assert.Equal("Создать", vm.Rows.Single().Kind);
            Assert.Equal("B", vm.Rows.Single().Subject);

            await vm.CopyCommand.ExecuteAsync();
            await vm.PreviewCommand.ExecuteAsync();
            Assert.True(vm.NothingToDo);
            Assert.Equal("Всё уже скопировано: 3 события совпадают", vm.Summary);
            Assert.False(vm.CopyCommand.CanExecute(null));
        }

        [Fact]
        public async Task Copy_NotConfirmed_NothingWritten_PreviewKept()
        {
            (CopyViewModel vm, TestSession target, FakeDialogs dialogs) = Create(null, Item("a", 10));
            await vm.PreviewCommand.ExecuteAsync();
            dialogs.AskAnswer = false;

            await vm.CopyCommand.ExecuteAsync();

            Assert.Empty(target.Copies.Copies);
            Assert.True(vm.HasPreview);
            Assert.False(vm.HasResult);
        }

        [Fact]
        public async Task Copy_NothingSelected_AsksToSelect()
        {
            (CopyViewModel vm, TestSession target, FakeDialogs dialogs) = Create(null, Item("a", 10));
            await vm.PreviewCommand.ExecuteAsync();
            vm.SelectNoneCommand.Execute(null);

            await vm.CopyCommand.ExecuteAsync();

            Assert.Empty(dialogs.Questions);
            Assert.Empty(target.Copies.Copies);
            Assert.Contains("ни одного", vm.ValidationError);
        }

        [Fact]
        public async Task ChangedInSource_Overwritten_DeletedInSource_CopyDeleted()
        {
            SourceItem kept = Item("a", 10, "Старая тема");
            SourceItem gone = Item("b", 11, "Отменено");
            (CopyViewModel vm, TestSession target, _) = Create(null);
            target.Copies.Seed(kept);
            target.Copies.Seed(gone);
            var source = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()))
            {
                SourceCalendar = new InMemorySource("источник", new[] { Item("a", 10, "Новая тема") }),
            };
            vm.SetEwsSource(source);

            await vm.PreviewCommand.ExecuteAsync();

            Assert.Equal("перезаписать 1, удалить копий 1", vm.Summary);
            Assert.Contains(vm.Rows, r => r.Kind == "Перезаписать" && r.Reason == "Изменилось в источнике");
            Assert.Contains(vm.Rows, r => r.Kind == "Удалить копию" && r.Reason == "Удалено в источнике");

            await vm.CopyCommand.ExecuteAsync();
            Assert.Equal("Новая тема", target.Copies.Copies.Single().Item.Subject);
        }

        [Fact]
        public async Task MassDelete_Blocked_UntilAllowed_ThenNewPreview()
        {
            (CopyViewModel vm, TestSession target, _) = Create(null, Item("keep", 5));
            for (int i = 0; i < 25; i++)
            {
                target.Copies.Seed(Item("old-" + i, 6 + i % 20));
            }

            await vm.PreviewCommand.ExecuteAsync();
            Assert.True(vm.IsBlocked);
            Assert.False(vm.CopyCommand.CanExecute(null));

            vm.AllowMassDelete = true;
            Assert.False(vm.HasPreview); // разрешение действует со следующего предпросмотра

            await vm.PreviewCommand.ExecuteAsync();
            Assert.False(vm.IsBlocked);
            Assert.True(vm.CopyCommand.CanExecute(null));
            await vm.CopyCommand.ExecuteAsync();
            Assert.Equal("keep", target.Copies.Copies.Single().Item.SourceId);
        }

        [Fact]
        public async Task OneItemFails_OthersWritten_FailureListed()
        {
            (CopyViewModel vm, TestSession target, _) = Create(null, Item("a", 10, "A"), Item("b", 11, "B"));
            target.Copies.FailOn = i => i.Subject == "B" ? new InvalidOperationException("нет места") : null;
            await vm.PreviewCommand.ExecuteAsync();

            await vm.CopyCommand.ExecuteAsync();

            Assert.Equal("A", target.Copies.Copies.Single().Item.Subject);
            Assert.Equal("Готово, но не всё", vm.ResultTitle);
            Assert.False(vm.ResultIsSuccess);
            Assert.Contains("Не удалось (1)", vm.ResultText);
            Assert.Contains("B", vm.ResultText);
        }

        [Fact]
        public async Task ConnectionFailure_StopsBatch_ShowsError()
        {
            (CopyViewModel vm, TestSession target, _) = Create(null, Item("a", 10, "A"), Item("b", 11, "B"), Item("c", 12, "C"));
            target.Copies.FailOn = _ => new InvalidOperationException("сервер недоступен");
            target.Fatal = _ => true;
            await vm.PreviewCommand.ExecuteAsync();

            await vm.CopyCommand.ExecuteAsync();

            Assert.Empty(target.Copies.Copies);
            Assert.Equal("Копирование остановлено ошибкой подключения", vm.ResultTitle);
            Assert.True(vm.HasError);
        }

        [Fact]
        public async Task Outlook_ListLoaded_FirstSelected_PreviewReadsIt()
        {
            var outlook = new FakeOutlook(new SourceChoice("Календарь — a@example.test", new InMemorySource("Outlook", new[] { Item("o", 15, "Из Outlook") })));
            (CopyViewModel vm, _, _) = Create(outlook);
            vm.UseOutlook = true;

            await vm.RefreshOutlookCommand.ExecuteAsync();
            await vm.PreviewCommand.ExecuteAsync();

            Assert.Equal("Календарь — a@example.test", vm.OutlookCalendar?.Label);
            Assert.Null(vm.OutlookError);
            Assert.Equal("Из Outlook", vm.Rows.Single().Subject);
        }

        [Fact]
        public async Task Outlook_OpenNowCalendar_SelectedByDefault()
        {
            var outlook = new FakeOutlook(
                new SourceChoice("Календарь — Мои календари", new InMemorySource("свой", Array.Empty<SourceItem>())),
                new SourceChoice("Иванов Иван Иванович — Общие календари", new InMemorySource("общий", new[] { Item("x", 15, "Общее") }), isCurrent: true));
            (CopyViewModel vm, _, _) = Create(outlook);
            vm.UseOutlook = true;

            await vm.RefreshOutlookCommand.ExecuteAsync();
            await vm.PreviewCommand.ExecuteAsync();

            Assert.Equal("Иванов Иван Иванович — Общие календари", vm.OutlookCalendar?.Label);
            Assert.Equal("Общее", vm.Rows.Single().Subject);
        }

        [Fact]
        public async Task Find_PartOfName_InList_SelectedWithoutAddressBook()
        {
            var outlook = new FakeOutlook(
                new SourceChoice("Календарь — Мои календари", new InMemorySource("свой", Array.Empty<SourceItem>()), isCurrent: true),
                new SourceChoice("Семёнова Анна — Общие календари", new InMemorySource("общий", Array.Empty<SourceItem>())));
            (CopyViewModel vm, _, _) = Create(outlook);
            await vm.RefreshOutlookCommand.ExecuteAsync();
            vm.UseEws = true;

            vm.CalendarSearch = "  СЕМЕНОВА ";
            await vm.FindOutlookCalendarCommand.ExecuteAsync();

            Assert.True(vm.UseOutlook); // поиск сам переключает источник на Outlook
            Assert.Equal("Семёнова Анна — Общие календари", vm.OutlookCalendar?.Label);
            Assert.Empty(outlook.Searched);
            Assert.Null(vm.OutlookError);
        }

        [Fact]
        public async Task Find_NotInList_AddressBook_AddedAndKeptAfterRefresh()
        {
            var outlook = new FakeOutlook(new SourceChoice("Календарь — Мои календари", new InMemorySource("свой", Array.Empty<SourceItem>()), isCurrent: true));
            outlook.Shared["Петров Пётр"] = new SourceChoice("Петров Пётр Петрович — общий календарь", new InMemorySource("общий", new[] { Item("p", 16, "Чужое") }));
            (CopyViewModel vm, _, _) = Create(outlook);
            await vm.RefreshOutlookCommand.ExecuteAsync();

            vm.CalendarSearch = "Петров Пётр";
            await vm.FindOutlookCalendarCommand.ExecuteAsync();
            Assert.Equal("Петров Пётр Петрович — общий календарь", vm.OutlookCalendar?.Label);

            await vm.RefreshOutlookCommand.ExecuteAsync();
            Assert.Equal(2, vm.OutlookCalendars.Count);
            Assert.Equal("Петров Пётр Петрович — общий календарь", vm.OutlookCalendar?.Label);
            await vm.PreviewCommand.ExecuteAsync();
            Assert.Equal("Чужое", vm.Rows.Single().Subject);
        }

        [Fact]
        public async Task Find_Unknown_ErrorShown_SelectionKept()
        {
            var outlook = new FakeOutlook(new SourceChoice("Календарь — Мои календари", new InMemorySource("свой", Array.Empty<SourceItem>()), isCurrent: true));
            (CopyViewModel vm, _, _) = Create(outlook);
            await vm.RefreshOutlookCommand.ExecuteAsync();
            Assert.False(vm.FindOutlookCalendarCommand.CanExecute(null)); // пустое поле — искать нечего

            vm.CalendarSearch = "Никто";
            await vm.FindOutlookCalendarCommand.ExecuteAsync();

            Assert.Contains("не найден", vm.OutlookError);
            Assert.Equal("Календарь — Мои календари", vm.OutlookCalendar?.Label);
        }

        [Fact]
        public async Task SourceWarnings_ShownInPreview()
        {
            var source = new WarningSource(Item("a", 10));
            (CopyViewModel vm, _, _) = Create(null);
            vm.SetEwsSource(new GateSession(source));

            await vm.PreviewCommand.ExecuteAsync();

            Assert.True(vm.HasPreview);
            Assert.Contains("не открывается", vm.WarningsText);
            Assert.Null(vm.SuppressedReason); // замечание — не неполное чтение: удаления не запрещаются
        }

        private sealed class WarningSource : ISourceCalendar
        {
            private readonly SourceItem[] _items;

            public WarningSource(params SourceItem[] items) => _items = items;

            public string Label => "источник с замечанием";

            public Task<SourceRead> ReadAsync(DateTime windowStartUtc, DateTime windowEndUtc, IReadOnlyCollection<string> referencedSourceIds, IProgress<int>? progress, CancellationToken cancellationToken) =>
                Task.FromResult(new SourceRead(_items, null, new[] { "Серия «Планёрка»: изменённое повторение от 01.06.2026 не открывается в Outlook — в копию не попадает." }));

            public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<AttachmentContent>>(Array.Empty<AttachmentContent>());
        }

        [Fact]
        public async Task Outlook_NotRunning_ErrorShown_PreviewUnavailable()
        {
            var outlook = new FakeOutlook { Failure = new InvalidOperationException("Outlook не запущен. Откройте Outlook и нажмите «Обновить».") };
            (CopyViewModel vm, _, _) = Create(outlook);
            vm.UseOutlook = true;

            await vm.RefreshOutlookCommand.ExecuteAsync();

            Assert.Contains("Outlook не запущен", vm.OutlookError);
            Assert.Empty(vm.OutlookCalendars);
            Assert.False(vm.PreviewCommand.CanExecute(null));
        }

        [Fact]
        public void NoOutlookInstalled_SourceIsEws()
        {
            var vm = new CopyViewModel(new FakeDialogs(), new FakeOutlook { Installed = false });

            Assert.False(vm.OutlookAvailable);
            Assert.True(vm.UseEws);
            Assert.False(vm.RefreshOutlookCommand.CanExecute(null));
        }

        [Fact]
        public async Task SwitchingSourceOrPeriod_DropsPreview()
        {
            var outlook = new FakeOutlook(new SourceChoice("Outlook", new InMemorySource("Outlook", Array.Empty<SourceItem>())));
            (CopyViewModel vm, _, _) = Create(outlook, Item("a", 10));
            await vm.PreviewCommand.ExecuteAsync();
            Assert.True(vm.HasPreview);

            vm.UseOutlook = true;
            Assert.False(vm.HasPreview);
            Assert.False(vm.CopyCommand.CanExecute(null));

            vm.UseEws = true;
            await vm.PreviewCommand.ExecuteAsync();
            vm.PeriodEnd = new DateTime(2026, 2, 28);
            Assert.False(vm.HasPreview);
        }

        [Fact]
        public async Task InvalidPeriod_Explained_NothingRead()
        {
            (CopyViewModel vm, _, _) = Create(null, Item("a", 10));
            vm.PeriodEnd = new DateTime(2025, 12, 1);

            await vm.PreviewCommand.ExecuteAsync();

            Assert.False(vm.HasPreview);
            Assert.Equal("Последний день периода раньше первого.", vm.ValidationError);
        }

        [Fact]
        public async Task Stop_DuringRead_PreviewNotOpened()
        {
            var gate = new GateSource();
            (CopyViewModel vm, _, _) = Create(null);
            vm.SetEwsSource(new GateSession(gate));

            Task preview = vm.PreviewCommand.ExecuteAsync();
            await gate.Entered.Task;
            Assert.True(vm.IsBusy);
            Assert.True(vm.StopCommand.CanExecute(null));

            vm.Stop();
            await preview;

            Assert.False(vm.IsBusy);
            Assert.False(vm.HasPreview);
            Assert.False(vm.HasError);
        }

        [Fact]
        public async Task SetTarget_WhileBusy_Throws()
        {
            var gate = new GateSource();
            (CopyViewModel vm, TestSession target, _) = Create(null);
            vm.SetEwsSource(new GateSession(gate));
            Task preview = vm.PreviewCommand.ExecuteAsync();
            await gate.Entered.Task;

            Assert.Throws<InvalidOperationException>(() => vm.SetTarget(target));

            vm.Stop();
            await preview;
        }

        /// <summary>Источник, чтение которого ждёт отмены — как долгое чтение большого календаря.</summary>
        internal sealed class GateSource : ISourceCalendar
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Label => "медленный источник";

            public async Task<SourceRead> ReadAsync(DateTime windowStartUtc, DateTime windowEndUtc, IReadOnlyCollection<string> referencedSourceIds, IProgress<int>? progress, CancellationToken cancellationToken)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("недостижимо");
            }

            public Task<IReadOnlyList<AttachmentContent>> LoadAttachmentsAsync(SourceItem item, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<AttachmentContent>>(Array.Empty<AttachmentContent>());
        }

        internal sealed class GateSession : ITargetSession
        {
            private readonly TestSession _inner = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));

            public GateSession(ISourceCalendar source) => Source = source;

            public ISourceCalendar Source { get; }

            public string TargetLabel => _inner.TargetLabel;

            public string ConfirmationPhrase => _inner.ConfirmationPhrase;

            public IwoBridge.Core.Time.TargetTimeZone TimeZone => _inner.TimeZone;

            public IwoBridge.App.Cleanup.CleanupRunner Runner => _inner.Runner;

            public bool IsDemo => false;

            public ICopyTarget CopyTarget => _inner.CopyTarget;

            public bool StopsBatch(Exception exception) => false;

            public IwoBridge.Wpf.Messages.UserMessage Describe(Exception exception) => _inner.Describe(exception);

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Календари «Outlook» для тестов: заданный список или ошибка (Outlook не открыт).</summary>
    internal sealed class FakeOutlook : IOutlookCalendars
    {
        private readonly SourceChoice[] _choices;

        public FakeOutlook(params SourceChoice[] choices) => _choices = choices;

        public bool Installed { get; set; } = true;

        public Exception? Failure { get; set; }

        public bool IsAvailable => Installed;

        public Task<IReadOnlyList<SourceChoice>> ListAsync()
        {
            if (Failure != null)
            {
                return Task.FromException<IReadOnlyList<SourceChoice>>(Failure);
            }

            return Task.FromResult<IReadOnlyList<SourceChoice>>(_choices);
        }

        /// <summary>Общие календари «адресной книги»: имя → календарь; кого нет — ошибка «не найден».</summary>
        public Dictionary<string, SourceChoice> Shared { get; } = new Dictionary<string, SourceChoice>(StringComparer.OrdinalIgnoreCase);

        public List<string> Searched { get; } = new List<string>();

        public Task<SourceChoice> FindSharedAsync(string owner)
        {
            Searched.Add(owner);
            return Shared.TryGetValue(owner, out SourceChoice? found)
                ? Task.FromResult(found)
                : Task.FromException<SourceChoice>(new InvalidOperationException("«" + owner + "» не найден ни среди календарей Outlook, ни в адресной книге."));
        }
    }
}
