using System;
using System.Linq;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.InMemory;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Экран очистки по шагам: параметры → предпросмотр → подтверждение → удаление → итог; правила безопасности
    /// (удаление только по актуальному предпросмотру, подтверждение вводом адреса, предпросмотр одноразовый).
    /// Пояс сеанса — UTC+03:00, период задаётся местными датами.
    /// </summary>
    public sealed class CleanupViewModelTests
    {
        private static DateTime Utc(int day, int hour) => new DateTime(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        private static InMemoryCalendar Calendar() => new InMemoryCalendar(new[]
        {
            InMemoryEvent.Single("in-1", Utc(12, 9), Utc(12, 10), subject: "Копия 1"),
            InMemoryEvent.Single("in-2", Utc(13, 9), Utc(13, 10), subject: "Копия 2"),
            InMemoryEvent.Single("out", Utc(25, 9), Utc(25, 10), subject: "Своё"),
            InMemoryEvent.Master("series", "U1", new[]
            {
                new InMemoryOccurrence("s-1", Utc(11, 9), Utc(11, 10)),
                new InMemoryOccurrence("s-2", Utc(18, 9), Utc(18, 10)),
            }, subject: "Серия"),
        });

        private static (CleanupViewModel Vm, TestSession Session, FakeDialogs Dialogs) Create(InMemoryCalendar? calendar = null)
        {
            var dialogs = new FakeDialogs();
            var session = new TestSession(calendar ?? Calendar());
            var vm = new CleanupViewModel(dialogs, () => new DateTime(2026, 1, 20));
            vm.SetSession(session);
            vm.IsPeriod = true;
            vm.PeriodStart = new DateTime(2026, 1, 10);
            vm.PeriodEnd = new DateTime(2026, 1, 19);
            return (vm, session, dialogs);
        }

        [Fact]
        public async Task Preview_ShowsWhatWillBeDeletedAndKept()
        {
            (CleanupViewModel vm, _, _) = Create();

            await vm.PreviewCommand.ExecuteAsync();

            Assert.Equal(CleanupStep.Reviewed, vm.Step);
            Assert.True(vm.HasPreview);
            Assert.Equal(2, vm.SingleCount);
            Assert.Equal(1, vm.WholeSeriesCount);
            Assert.Equal("2 события и 1 серия целиком", vm.Summary);
            Assert.Equal(3, vm.DeleteRowCount);
            Assert.Equal(1, vm.KeptRowCount);
            Assert.Contains(vm.DeleteRows, r => r.Subject == "Копия 1" && r.Start == "12.01.2026 12:00" && r.Kind == "Событие");
            Assert.Equal("Вне периода", vm.KeptRows.Single().Reason);
            Assert.True(vm.DeleteCommand.CanExecute(null));
        }

        [Fact]
        public async Task ChangingPeriodAfterPreview_DropsPreview_DeleteUnavailable()
        {
            (CleanupViewModel vm, _, _) = Create();
            await vm.PreviewCommand.ExecuteAsync();

            vm.PeriodEnd = new DateTime(2026, 1, 31);

            Assert.False(vm.HasPreview);
            Assert.Equal(CleanupStep.Choose, vm.Step);
            Assert.False(vm.DeleteCommand.CanExecute(null));
        }

        [Fact]
        public async Task Delete_NotConfirmed_NothingDeleted_PreviewKept()
        {
            (CleanupViewModel vm, TestSession session, FakeDialogs dialogs) = Create();
            await vm.PreviewCommand.ExecuteAsync();
            dialogs.ConfirmAnswer = false;

            await vm.DeleteCommand.ExecuteAsync();

            Assert.Equal(4, session.Calendar.ItemIds().Count);
            Assert.Equal(CleanupStep.Reviewed, vm.Step);
            Assert.Equal(0, session.Manifest.Writes);
        }

        [Fact]
        public async Task Delete_WrongPhrase_ConfirmDisabled_NothingDeleted()
        {
            (CleanupViewModel vm, TestSession session, FakeDialogs dialogs) = Create();
            await vm.PreviewCommand.ExecuteAsync();
            dialogs.TypeOnConfirm = "other@example.test";

            await vm.DeleteCommand.ExecuteAsync();

            Assert.Equal(4, session.Calendar.ItemIds().Count);
        }

        [Fact]
        public async Task Delete_Confirmed_DeletesShowsResult_PreviewUsedUp()
        {
            (CleanupViewModel vm, TestSession session, FakeDialogs dialogs) = Create();
            await vm.PreviewCommand.ExecuteAsync();
            dialogs.TypeOnConfirm = "  USER@example.test ";

            await vm.DeleteCommand.ExecuteAsync();

            Assert.Equal(new[] { "out" }, session.Calendar.ItemIds());
            Assert.Equal(CleanupStep.Finished, vm.Step);
            Assert.True(vm.ResultIsSuccess);
            Assert.Equal("Готово", vm.ResultTitle);
            Assert.Contains("Удалённые", vm.ResultText);
            Assert.Equal("memory://manifest.csv", vm.ManifestPath);
            Assert.False(vm.DeleteCommand.CanExecute(null));
            Assert.True(vm.PreviewCommand.CanExecute(null));

            vm.ShowManifestCommand.Execute(null);
            Assert.Equal("memory://manifest.csv", dialogs.ShownFile);
        }

        [Fact]
        public async Task HardDelete_ConfirmationRequiresAcknowledgement()
        {
            (CleanupViewModel vm, TestSession session, FakeDialogs dialogs) = Create();
            await vm.PreviewCommand.ExecuteAsync();
            vm.HardDelete = true;
            dialogs.TypeOnConfirm = "user@example.test";

            await vm.DeleteCommand.ExecuteAsync();

            Assert.True(dialogs.LastConfirmation!.HardDelete);
            Assert.Equal(4, session.Calendar.ItemIds().Count);
        }

        [Fact]
        public async Task WholeCalendar_DeletesEverything()
        {
            (CleanupViewModel vm, TestSession session, _) = Create();
            vm.IsWholeCalendar = true;

            await vm.PreviewCommand.ExecuteAsync();
            await vm.DeleteCommand.ExecuteAsync();

            Assert.Empty(session.Calendar.ItemIds());
            Assert.Contains("4", vm.ResultText);
        }

        [Fact]
        public async Task PeriodEndBeforeStart_ValidationError_NoPreview()
        {
            (CleanupViewModel vm, _, _) = Create();
            vm.PeriodEnd = new DateTime(2026, 1, 5);

            await vm.PreviewCommand.ExecuteAsync();

            Assert.Equal("Последний день периода раньше первого.", vm.ValidationError);
            Assert.False(vm.HasPreview);
        }

        [Fact]
        public async Task MissingDates_ValidationError()
        {
            (CleanupViewModel vm, _, _) = Create();
            vm.PeriodStart = null;

            await vm.PreviewCommand.ExecuteAsync();

            Assert.NotNull(vm.ValidationError);
        }

        [Fact]
        public async Task ReadFailure_ShowsError_StaysOnChoose()
        {
            var dialogs = new FakeDialogs();
            var vm = new CleanupViewModel(dialogs);
            vm.SetSession(new TestSession(Calendar(), new FailingPageReader()));
            vm.IsWholeCalendar = true;

            await vm.PreviewCommand.ExecuteAsync();

            Assert.True(vm.HasError);
            Assert.Equal("Ошибка теста", vm.Error!.Title);
            Assert.Equal("сервер недоступен", vm.Error.Details);
            Assert.Equal(CleanupStep.Choose, vm.Step);
            Assert.False(vm.IsBusy);
        }

        [Fact]
        public async Task NothingInPeriod_InfoShown_DeleteUnavailable()
        {
            (CleanupViewModel vm, _, _) = Create();
            vm.PeriodStart = new DateTime(2026, 3, 1);
            vm.PeriodEnd = new DateTime(2026, 3, 2);

            await vm.PreviewCommand.ExecuteAsync();

            Assert.True(vm.NothingToDelete);
            Assert.Equal("Удалять нечего", vm.Summary);
            Assert.False(vm.DeleteCommand.CanExecute(null));
        }

        [Fact]
        public async Task NewSession_DropsPreview()
        {
            (CleanupViewModel vm, _, _) = Create();
            await vm.PreviewCommand.ExecuteAsync();

            vm.SetSession(new TestSession(Calendar()));

            Assert.False(vm.HasPreview);
            Assert.False(vm.DeleteCommand.CanExecute(null));
        }

        [Fact]
        public void NoSession_PreviewUnavailable()
        {
            var vm = new CleanupViewModel(new FakeDialogs());

            Assert.False(vm.HasSession);
            Assert.False(vm.PreviewCommand.CanExecute(null));
        }

        [Fact]
        public void QuickPeriod_LastMonth()
        {
            (CleanupViewModel vm, _, _) = Create();
            vm.IsWholeCalendar = true;

            vm.SetLastMonthCommand.Execute(null);

            Assert.True(vm.IsPeriod);
            Assert.Equal(new DateTime(2025, 12, 20), vm.PeriodStart);
            Assert.Equal(new DateTime(2026, 1, 20), vm.PeriodEnd);
        }

        [Theory]
        [InlineData(CleanupStage.Deleting, 3, 10, "Удаляю: 3 из 10")]
        [InlineData(CleanupStage.ReadingCalendar, 1500, 12000, "Читаю календарь: 1\u00A0500 из 12\u00A0000")]
        [InlineData(CleanupStage.ReadingCalendar, 0, 0, "Читаю календарь…")]
        public void DescribeProgress_ReadableText(CleanupStage stage, int done, int total, string expected)
        {
            Assert.Equal(expected, CleanupViewModel.DescribeProgress(new CleanupProgress(stage, done, total)));
        }
    }
}
