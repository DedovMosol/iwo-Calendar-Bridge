using System;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Поиск вхождения своей серии в ответе CalendarView: не соседний single, UID без учёта регистра,
    /// «на весь день» — по местной дате, обычное — допуск 12 часов.
    /// </summary>
    public sealed class OccurrencePickTests
    {
        private static readonly TargetTimeZone Zone = TargetTimeZone.FromHours(3);
        private const string Uid = "series-uid";

        private static OccurrencePick.Candidate C(string? uid, DateTime startUtc, bool series = true) =>
            new OccurrencePick.Candidate(uid, startUtc, series);

        [Fact]
        public void NeighborSingle_SameTime_NotPicked()
        {
            DateTime at = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            int? index = OccurrencePick.IndexOf(
                new[] { C("other", at, series: false), C(Uid, at) },
                Uid,
                at,
                isAllDay: false,
                Zone);

            Assert.Equal(1, index);
        }

        [Fact]
        public void ForeignSeries_SameDay_NotPicked()
        {
            DateTime at = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            Assert.Null(OccurrencePick.IndexOf(
                new[] { C("other-series", at) },
                Uid,
                at,
                isAllDay: false,
                Zone));
        }

        [Fact]
        public void Uid_CaseInsensitive()
        {
            DateTime at = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            Assert.Equal(0, OccurrencePick.IndexOf(
                new[] { C("SERIES-UID", at) },
                "series-uid",
                at,
                isAllDay: false,
                Zone));
        }

        [Fact]
        public void EmptyUid_NeverMatches()
        {
            DateTime at = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            Assert.Null(OccurrencePick.IndexOf(
                new[] { C("", at), C(null, at) },
                Uid,
                at,
                isAllDay: false,
                Zone));
            Assert.Null(OccurrencePick.IndexOf(new[] { C(Uid, at) }, "", at, false, Zone));
        }

        [Fact]
        public void Timed_PicksCloserWithin12Hours()
        {
            DateTime expected = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            int? index = OccurrencePick.IndexOf(
                new[]
                {
                    C(Uid, expected.AddHours(-23)),
                    C(Uid, expected.AddHours(1)),
                    C(Uid, expected.AddHours(23)),
                },
                Uid,
                expected,
                isAllDay: false,
                Zone);

            Assert.Equal(1, index);
        }

        [Fact]
        public void Timed_13Hours_NotPicked()
        {
            DateTime expected = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            Assert.Null(OccurrencePick.IndexOf(
                new[] { C(Uid, expected.AddHours(13)) },
                Uid,
                expected,
                isAllDay: false,
                Zone));
        }

        [Fact]
        public void AllDay_MatchesLocalDate_NotNeighborDay()
        {
            // 7 октября в +03:00 — с 6 октября 21:00 UTC.
            DateTime expected = new DateTime(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc);
            DateTime sameDay = new DateTime(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc);
            DateTime previous = new DateTime(2026, 10, 5, 21, 0, 0, DateTimeKind.Utc);
            int? index = OccurrencePick.IndexOf(
                new[] { C(Uid, previous), C(Uid, sameDay) },
                Uid,
                expected,
                isAllDay: true,
                Zone);

            Assert.Equal(1, index);
        }
    }
}
