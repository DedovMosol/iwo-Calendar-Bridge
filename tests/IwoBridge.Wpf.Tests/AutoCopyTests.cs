using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Sync;
using IwoBridge.InMemory;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.ViewModels;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>
    /// Автокопирование «раз в N» при долгой работе: расписание (без накладок, пауза при ошибке входа, одно уведомление
    /// на одну проблему), запуск ждёт открытый вопрос и очистку, период сдвигается с датой, Outlook, открытый позже
    /// программы, подхватывается, а выбранный календарь не подменяется другим.
    /// </summary>
    public sealed class AutoCopyTests
    {
        private static DateTime Utc(int day, int hour) => new DateTime(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        private static SourceItem Item(string id, int day) => new SourceItem(id, "Событие " + id, null, null, Utc(day, 9), Utc(day, 10));

        // ---- Расписание ----

        [Fact]
        public async Task Schedule_FirstRunAfterMinute_NextAfterInterval_NoOverlap()
        {
            var timer = new FakeTimer();
            int runs = 0;
            var auto = new AutoCopyViewModel(timer, null, () => { runs++; return Task.FromResult(new AutoRunOutcome("изменений нет", false)); }, () => { }, true, 15);

            Assert.Equal(AutoCopyViewModel.FirstDelay, timer.Due);
            await timer.FireAsync();

            Assert.Equal(1, runs);
            Assert.Equal(TimeSpan.FromMinutes(15), timer.Due);
            Assert.Contains("изменений нет", auto.StatusText);
        }

        [Fact]
        public async Task SystemResumed_NotDueKeepsInterval_OverdueMovedToMinute_PausedStaysPaused()
        {
            var timer = new FakeTimer();
            var clock = new DateTime(2026, 1, 10, 8, 0, 0);
            var auto = new AutoCopyViewModel(
                timer, null, () => Task.FromResult(new AutoRunOutcome("изменений нет", false)), () => { }, true, 240, () => clock);
            await timer.FireAsync();
            Assert.Equal(TimeSpan.FromMinutes(240), timer.Due);

            clock = clock.AddMinutes(30); // проснулся через полчаса: до запуска ещё 3,5 часа
            auto.SystemResumed();
            Assert.Equal(TimeSpan.FromMinutes(210), timer.Due);

            clock = clock.AddHours(5); // проспал срок — через минуту, когда поднимется сеть
            auto.SystemResumed();
            Assert.Equal(AutoCopyViewModel.FirstDelay, timer.Due);

            var pausedTimer = new FakeTimer();
            var paused = new AutoCopyViewModel(pausedTimer, null, () => Task.FromResult(new AutoRunOutcome("неверный пароль", true, needsAttention: true)), () => { }, true, 5);
            await pausedTimer.FireAsync();
            TimeSpan? before = pausedTimer.Due;
            paused.SystemResumed();
            Assert.True(paused.IsPaused);
            Assert.Equal(before, pausedTimer.Due);
        }

        [Fact]
        public async Task RunNow_Skipped_PersonToldWhen()
        {
            var timer = new FakeTimer();
            var auto = new AutoCopyViewModel(timer, null, () => Task.FromResult(new AutoRunOutcome("занято", false, skipped: true)), () => { }, true, 60);
            string? told = null;
            auto.Postponed += (_, message) => told = message;

            await auto.RunNowCommand.ExecuteAsync();

            Assert.Contains("через минуту", told);
        }

        [Fact]
        public async Task Skipped_RetriedInMinute()
        {
            var timer = new FakeTimer();
            var auto = new AutoCopyViewModel(timer, null, () => Task.FromResult(new AutoRunOutcome("занято", false, skipped: true)), () => { }, true, 60);

            await timer.FireAsync();

            Assert.Equal(AutoCopyViewModel.BusyRetry, timer.Due);
            Assert.DoesNotContain("занято", auto.StatusText); // пропуск — не запуск
        }

        [Fact]
        public async Task NeedsAttention_Paused_NoRetries_ResumeAfterFix()
        {
            var timer = new FakeTimer();
            int runs = 0;
            var auto = new AutoCopyViewModel(
                timer, null, () => { runs++; return Task.FromResult(new AutoRunOutcome("неверный пароль", true, needsAttention: true)); }, () => { }, true, 5);

            await timer.FireAsync();

            Assert.True(auto.IsPaused);
            Assert.False(timer.Armed); // ни одной новой попытки входа
            Assert.False(auto.RunNowCommand.CanExecute(null));
            Assert.Contains("приостановлено", auto.StatusText);

            auto.Resume();
            Assert.False(auto.IsPaused);
            Assert.Equal(AutoCopyViewModel.FirstDelay, timer.Due);
            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task SameProblem_NotifiedOnce_NewProblemNotifiedAgain()
        {
            var timer = new FakeTimer();
            string summary = "нет связи";
            var auto = new AutoCopyViewModel(timer, null, () => Task.FromResult(new AutoRunOutcome(summary, true)), () => { }, true, 5);
            var shown = new List<string>();
            auto.Problem += (_, o) => shown.Add(o.Summary);

            await timer.FireAsync();
            await timer.FireAsync();
            summary = "Outlook не запущен";
            await timer.FireAsync();

            Assert.Equal(new[] { "нет связи", "Outlook не запущен" }, shown);
        }

        [Fact]
        public void Disable_CancelsTimer_Dispose_Unsubscribes()
        {
            var timer = new FakeTimer();
            var auto = new AutoCopyViewModel(timer, null, () => Task.FromResult(new AutoRunOutcome("", false)), () => { }, true, 5);

            auto.IsEnabled = false;
            Assert.False(timer.Armed);

            auto.Dispose();
            Assert.Equal(0, timer.Subscribers);
            Assert.True(timer.Disposed);
        }

        // ---- Запуск: что он ждёт и что делает ----

        [Fact]
        public async Task OpenDialog_RunSkipped_NothingWritten()
        {
            (CopyViewModel vm, TestSession target, FakeDialogs dialogs, _) = Create(Item("a", 10));
            dialogs.IsShowingDialog = true; // например, открыт вопрос «Скопировать?»

            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            Assert.True(outcome.Skipped);
            Assert.Empty(target.Copies.Copies);
        }

        [Fact]
        public async Task CleanupRunning_RunSkipped()
        {
            bool cleanupBusy = true;
            (CopyViewModel vm, TestSession target, _, _) = Create(new[] { Item("a", 10) }, otherBusy: () => cleanupBusy);

            Assert.True((await vm.RunScheduledAsync()).Skipped);
            cleanupBusy = false;
            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            Assert.False(outcome.Skipped);
            Assert.Single(target.Copies.Copies);
        }

        [Fact]
        public async Task Run_WritesRecommended_SkipsDuplicateOfOwn_ClosesOpenPreview()
        {
            (CopyViewModel vm, TestSession target, _, _) = Create(Item("a", 10), Item("b", 11));
            target.Copies.Own.Add(new OwnEvent("Событие b", Utc(11, 9), Utc(11, 10), Core.Model.ItemKind.Single));
            await vm.PreviewCommand.ExecuteAsync();
            Assert.True(vm.HasPreview);

            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            Assert.False(outcome.IsProblem, outcome.Summary);
            Assert.Equal("a", target.Copies.Copies.Single().Item.SourceId); // свой двойник «b» не продублирован
            Assert.False(vm.HasPreview); // устаревший предпросмотр закрыт — его выполнение дало бы дубли
            Assert.StartsWith("Автокопирование в ", vm.ResultTitle);
        }

        [Fact]
        public async Task RollingPeriod_ShiftsWithDate()
        {
            DateTime today = new DateTime(2026, 1, 20);
            var store = new MemoryCopySettingsStore(new CopySettings(fromOutlook: false, daysBack: 5, daysForward: 3));
            (CopyViewModel vm, TestSession target, _, _) = Create(new[] { Item("old", 16), Item("later", 24) }, () => today, store);

            await vm.RunScheduledAsync();
            Assert.Equal(new[] { "old" }, target.Copies.Copies.Select(c => c.Item.SourceId));

            today = today.AddDays(4); // программа работает дальше — период сдвинулся: 19–27 января
            await vm.RunScheduledAsync();

            Assert.Equal(new DateTime(2026, 1, 19), vm.PeriodStart);
            Assert.Equal(new DateTime(2026, 1, 27), vm.PeriodEnd);
            Assert.Contains(target.Copies.Copies, c => c.Item.SourceId == "later");
            Assert.Equal(5, store.Current.DaysBack); // запомненные дни не «съезжают»
            Assert.Equal(3, store.Current.DaysForward);
        }

        [Fact]
        public async Task AllTime_Remembered_AutoCopyCopiesWholeCalendar()
        {
            var store = new MemoryCopySettingsStore(new CopySettings(fromOutlook: false, daysBack: 5, daysForward: 3));
            var past = new SourceItem("past", "Давнее", null, null, new DateTime(2003, 5, 5, 9, 0, 0, DateTimeKind.Utc), new DateTime(2003, 5, 5, 10, 0, 0, DateTimeKind.Utc));
            (CopyViewModel vm, TestSession target, _, _) = Create(new[] { Item("near", 18), past }, store: store);

            vm.IsAllTime = true;
            Assert.True(store.Current.AllTime);
            Assert.Equal(5, store.Current.DaysBack); // дни для периода по датам не потеряны

            await vm.RunScheduledAsync();

            Assert.Equal(new[] { "near", "past" }, target.Copies.Copies.Select(c => c.Item.SourceId).OrderBy(s => s, StringComparer.Ordinal));
            Assert.True(vm.IsAllTime);

            // После перезапуска программы — снова «всё время».
            (CopyViewModel reopened, _, _, _) = Create(Array.Empty<SourceItem>(), store: store);
            Assert.True(reopened.IsAllTime);
        }

        [Fact]
        public async Task ConnectionNeedsAttention_ReportedAsPause()
        {
            (CopyViewModel vm, TestSession target, _, _) = Create(Item("a", 10));
            target.Copies.FailOn = _ => new UnauthorizedAccessException("неверный пароль");
            target.Fatal = _ => true;
            target.Attention = _ => true;

            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            Assert.True(outcome.NeedsAttention);
        }

        [Fact]
        public async Task OutlookStartedLater_CalendarPickedUpOnNextRun()
        {
            var outlook = new FakeOutlook { Failure = new InvalidOperationException("Outlook не запущен.") };
            var store = new MemoryCopySettingsStore(new CopySettings(fromOutlook: true, outlookCalendar: "Петров — Общие календари", outlookCalendarKey: "ID-P"));
            (CopyViewModel vm, TestSession target, _, _) = Create(Array.Empty<SourceItem>(), store: store, outlook: outlook);

            AutoRunOutcome first = await vm.RunScheduledAsync();
            Assert.True(first.IsProblem);
            Assert.Contains("не запущен", first.Summary);

            // Outlook открылся; подпись другая (нет окна — нет групп панели), идентификатор папки тот же.
            outlook.Failure = null;
            outlook.Choices = new[]
            {
                new SourceChoice("Календарь — me@example.test", new InMemorySource("свой", new[] { Item("mine", 12) }), isCurrent: true, key: "ID-ME"),
                new SourceChoice("Календарь — petrov@example.test", new InMemorySource("Петров", new[] { Item("p", 12) }), key: "ID-P"),
            };
            AutoRunOutcome second = await vm.RunScheduledAsync();

            Assert.False(second.IsProblem, second.Summary);
            Assert.Equal("p", target.Copies.Copies.Single().Item.SourceId);
        }

        [Fact]
        public async Task SavedCalendarGone_NotSubstituted()
        {
            var outlook = new FakeOutlook(new SourceChoice("Календарь — me@example.test", new InMemorySource("свой", new[] { Item("mine", 12) }), isCurrent: true, key: "ID-ME"));
            var store = new MemoryCopySettingsStore(new CopySettings(fromOutlook: true, outlookCalendar: "Петров — Общие календари", outlookCalendarKey: "ID-P"));
            (CopyViewModel vm, TestSession target, _, _) = Create(Array.Empty<SourceItem>(), store: store, outlook: outlook);

            AutoRunOutcome outcome = await vm.RunScheduledAsync();

            Assert.True(outcome.IsProblem);
            Assert.Contains("Петров", outcome.Summary);
            Assert.Empty(target.Copies.Copies); // свой календарь в чужой ящик молча не ушёл
            Assert.Equal("ID-P", store.Current.OutlookCalendarKey);
        }

        [Fact]
        public async Task SameMailbox_Refused()
        {
            (CopyViewModel vm, TestSession target, _, _) = Create(Item("a", 10));
            var self = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>())) { TargetLabel = target.TargetLabel };
            vm.SetEwsSource(self);

            AutoRunOutcome outcome = await vm.RunScheduledAsync();
            await vm.PreviewCommand.ExecuteAsync();

            Assert.True(outcome.NeedsAttention);
            Assert.False(vm.HasPreview);
            Assert.Contains("один и тот же ящик", vm.ValidationError);
        }

        private static (CopyViewModel Vm, TestSession Target, FakeDialogs Dialogs, MemoryCopySettingsStore Store) Create(params SourceItem[] items) =>
            Create(items, null, null, null, null);

        private static (CopyViewModel Vm, TestSession Target, FakeDialogs Dialogs, MemoryCopySettingsStore Store) Create(
            SourceItem[] items,
            Func<DateTime>? today = null,
            MemoryCopySettingsStore? store = null,
            Func<bool>? otherBusy = null,
            IOutlookCalendars? outlook = null)
        {
            var dialogs = new FakeDialogs { AskAnswer = true };
            store = store ?? new MemoryCopySettingsStore(new CopySettings(fromOutlook: false));
            var target = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()));
            var source = new TestSession(new InMemoryCalendar(Array.Empty<InMemoryEvent>()))
            {
                SourceCalendar = new InMemorySource("источник по EWS", items),
                TargetLabel = CopyViewModelTests.SourceMailbox,
            };
            var vm = new CopyViewModel(dialogs, outlook, today ?? (() => new DateTime(2026, 1, 20)), store, null, null, otherBusy);
            vm.SetTarget(target);
            vm.SetEwsSource(source);
            return (vm, target, dialogs, store);
        }

        /// <summary>Таймер, который срабатывает по команде теста.</summary>
        private sealed class FakeTimer : IOneShotTimer
        {
            private EventHandler? _elapsed;

            public event EventHandler? Elapsed
            {
                add { _elapsed += value; Subscribers++; }
                remove { _elapsed -= value; Subscribers--; }
            }

            public TimeSpan? Due { get; private set; }

            public bool Armed => Due.HasValue;

            public int Subscribers { get; private set; }

            public bool Disposed { get; private set; }

            public void Schedule(TimeSpan due) => Due = due;

            public void Cancel() => Due = null;

            public void Dispose() => Disposed = true;

            /// <summary>Сработать и дождаться обработчика (он async void — запуск идёт в нём).</summary>
            public async Task FireAsync()
            {
                Due = null;
                _elapsed?.Invoke(this, EventArgs.Empty);
                for (int i = 0; i < 50 && !Armed; i++)
                {
                    await Task.Delay(10).ConfigureAwait(true);
                }
            }
        }
    }
}
