using System;
using IwoBridge.Core.Time;
using Xunit;
using static IwoBridge.Core.Tests.TestData;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Единое правило попадания события в период [начало, конец): границы окна, события нулевой длительности, событие, заканчивающееся ровно в начале окна, не попадает.
    /// </summary>
    public sealed class TimeWindowTests
    {
        // Окно [10 янв 00:00, 20 янв 00:00) UTC.
        private static readonly DateTime WindowStart = Utc(2026, 1, 10);
        private static readonly DateTime WindowEnd = Utc(2026, 1, 20);

        private static bool Overlaps(DateTime start, DateTime end) => TimeWindow.Overlaps(start, end, WindowStart, WindowEnd);

        [Fact]
        public void Inside_Overlaps() => Assert.True(Overlaps(Utc(2026, 1, 12, 9), Utc(2026, 1, 12, 10)));

        [Fact]
        public void EndingExactlyAtWindowStart_DoesNotOverlap() => Assert.False(Overlaps(Utc(2026, 1, 9, 23), WindowStart));

        [Fact]
        public void StartingExactlyAtWindowEnd_DoesNotOverlap() => Assert.False(Overlaps(WindowEnd, Utc(2026, 1, 20, 1)));

        [Fact]
        public void CrossingWindowStart_Overlaps() => Assert.True(Overlaps(Utc(2026, 1, 9, 23), Utc(2026, 1, 10, 1)));

        [Fact]
        public void CrossingWindowEnd_Overlaps() => Assert.True(Overlaps(Utc(2026, 1, 19, 23), Utc(2026, 1, 20, 1)));

        [Fact]
        public void CoveringWholeWindow_Overlaps() => Assert.True(Overlaps(Utc(2026, 1, 1), Utc(2026, 2, 1)));

        [Fact]
        public void ZeroDurationAtWindowStart_Overlaps() => Assert.True(Overlaps(WindowStart, WindowStart));

        [Fact]
        public void ZeroDurationAtWindowEnd_DoesNotOverlap() => Assert.False(Overlaps(WindowEnd, WindowEnd));

        [Fact]
        public void EndBeforeStart_TreatedAsPointAtStart()
        {
            Assert.True(Overlaps(Utc(2026, 1, 12), Utc(2026, 1, 11)));
            Assert.False(Overlaps(Utc(2026, 1, 25), Utc(2026, 1, 12)));
        }

        [Fact]
        public void UnspecifiedKind_TreatedAsUtc()
        {
            var start = new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Unspecified);
            Assert.True(Overlaps(start, start.AddHours(1)));
        }
    }
}
