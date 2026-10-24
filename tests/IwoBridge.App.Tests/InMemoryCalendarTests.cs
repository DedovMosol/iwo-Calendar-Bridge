using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Model;
using IwoBridge.Core.Paging;
using IwoBridge.Core.Reading;
using IwoBridge.InMemory;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>Проверка, что ин-мемори адаптер воспроизводит важные для логики особенности EWS.</summary>
    public sealed class InMemoryCalendarTests
    {
        private static DateTime Utc(int month, int day, int hour = 9) => new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime WindowStart = Utc(1, 10, 0);
        private static readonly DateTime WindowEnd = Utc(1, 20, 0);

        private static InMemoryCalendar Series(params InMemoryOccurrence[] occurrences) =>
            new InMemoryCalendar(new[] { InMemoryEvent.Master("m", "U", occurrences) });

        [Fact]
        public async Task ReadOccurrences_IncludesInstanceEndingExactlyAtWindowStart_LikeCalendarView()
        {
            InMemoryCalendar calendar = Series(new InMemoryOccurrence("edge", Utc(1, 9, 23), WindowStart));

            OccurrenceReadResult read = await calendar.ReadOccurrencesAsync(WindowStart, WindowEnd, CancellationToken.None);

            Assert.Equal(new[] { "edge" }, read.Occurrences.Select(o => o.OccurrenceItemId));
            Assert.True(read.Occurrences.Single().IsSeriesInstance);
        }

        [Fact]
        public async Task ReadOccurrences_ExcludesInstanceStartingAtWindowEnd()
        {
            InMemoryCalendar calendar = Series(new InMemoryOccurrence("late", WindowEnd, WindowEnd.AddHours(1)));

            OccurrenceReadResult read = await calendar.ReadOccurrencesAsync(WindowStart, WindowEnd, CancellationToken.None);

            Assert.Empty(read.Occurrences);
        }

        [Fact]
        public async Task ReadPage_DoesNotExposeSeriesData_LikeFindItem()
        {
            InMemoryCalendar calendar = Series(new InMemoryOccurrence("o1", Utc(1, 12), Utc(1, 12, 10)));

            TargetPage page = await calendar.ReadPageAsync(0, 10, CancellationToken.None);

            TargetItemInfo master = page.Items.Single();
            Assert.Equal(ItemKind.RecurringMaster, master.Kind);
            Assert.Null(master.FirstOccurrenceUtc);
            Assert.Null(master.Uid);
            Assert.True(page.IncludesLastItemInRange);
        }

        [Fact]
        public async Task LoadAsync_ReturnsRuleBoundsAndModifiedOccurrences()
        {
            InMemoryCalendar calendar = Series(
                new InMemoryOccurrence("o1", Utc(1, 12), Utc(1, 12, 10)),
                new InMemoryOccurrence("o2", Utc(1, 16), Utc(1, 16, 10)),
                new InMemoryOccurrence("moved", Utc(1, 25), Utc(1, 25, 10), isModified: true));

            IReadOnlyDictionary<string, RecurrenceInfo> info = await calendar.LoadAsync(new[] { "m" }, CancellationToken.None);

            RecurrenceInfo recurrence = info["m"];
            Assert.Equal(Utc(1, 12), recurrence.FirstStartUtc);
            Assert.Equal(Utc(1, 16), recurrence.LastStartUtc);
            Assert.Equal("U", recurrence.Uid);
            Assert.Equal(Utc(1, 25), recurrence.ModifiedOccurrences.Single().StartUtc);
        }

        [Fact]
        public async Task Delete_UnknownId_IsAlreadyAbsent()
        {
            InMemoryCalendar calendar = Series(new InMemoryOccurrence("o1", Utc(1, 12), Utc(1, 12, 10)));

            IReadOnlyList<DeleteResult> results = await calendar.DeleteAsync(
                new[] { "missing" }, TargetDeleteMode.MoveToDeletedItems, CancellationToken.None);

            Assert.Equal(DeleteItemStatus.AlreadyAbsent, results.Single().Status);
        }

        [Fact]
        public void Add_DuplicateId_Throws()
        {
            InMemoryCalendar calendar = Series(new InMemoryOccurrence("o1", Utc(1, 12), Utc(1, 12, 10)));

            Assert.Throws<ArgumentException>(() =>
                calendar.Add(InMemoryEvent.Single("m", Utc(1, 13), Utc(1, 13, 10))));
        }
    }
}
