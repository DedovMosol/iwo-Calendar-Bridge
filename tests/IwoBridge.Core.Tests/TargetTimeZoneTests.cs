using System;
using IwoBridge.Core.Time;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Смещения нейтральные и «неудобные»: дробное +05:30 и отрицательное −05:00 — проверяют минутную точность
    /// и переход через границу суток в обе стороны.
    /// </summary>
    public sealed class TargetTimeZoneTests
    {
        private static readonly TimeSpan PlusFiveThirty = new TimeSpan(5, 30, 0);

        [Fact]
        public void FromOffset_KeepsFractionalOffset()
        {
            Assert.Equal(PlusFiveThirty, TargetTimeZone.FromOffset(PlusFiveThirty).Offset);
        }

        [Fact]
        public void FromHours_KeepsNegativeOffset()
        {
            Assert.Equal(TimeSpan.FromHours(-5), TargetTimeZone.FromHours(-5).Offset);
        }

        [Fact]
        public void Utc_HasZeroOffset()
        {
            Assert.Equal(TimeSpan.Zero, TargetTimeZone.Utc.Offset);
        }

        [Theory]
        [InlineData(15)]
        [InlineData(-15)]
        public void FromHours_OutOfRange_Throws(int hours)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TargetTimeZone.FromHours(hours));
        }

        [Fact]
        public void FromOffset_NonWholeMinute_Throws()
        {
            Assert.Throws<ArgumentException>(() => TargetTimeZone.FromOffset(TimeSpan.FromSeconds(90)));
        }

        [Fact]
        public void PeriodFromLocalDates_SingleDay_PositiveFractionalOffset()
        {
            TargetTimeZone tz = TargetTimeZone.FromOffset(PlusFiveThirty);

            (DateTime startUtc, DateTime endUtcExclusive) = tz.PeriodFromLocalDates(
                new DateTime(2026, 1, 10), new DateTime(2026, 1, 10));

            Assert.Equal(new DateTime(2026, 1, 9, 18, 30, 0, DateTimeKind.Utc), startUtc);
            Assert.Equal(new DateTime(2026, 1, 10, 18, 30, 0, DateTimeKind.Utc), endUtcExclusive);
            Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, endUtcExclusive.Kind);
        }

        [Fact]
        public void PeriodFromLocalDates_SingleDay_NegativeOffset()
        {
            TargetTimeZone tz = TargetTimeZone.FromHours(-5);

            (DateTime startUtc, DateTime endUtcExclusive) = tz.PeriodFromLocalDates(
                new DateTime(2026, 1, 10), new DateTime(2026, 1, 10));

            Assert.Equal(new DateTime(2026, 1, 10, 5, 0, 0, DateTimeKind.Utc), startUtc);
            Assert.Equal(new DateTime(2026, 1, 11, 5, 0, 0, DateTimeKind.Utc), endUtcExclusive);
        }

        [Fact]
        public void PeriodFromLocalDates_UsesDatePartOnly()
        {
            TargetTimeZone tz = TargetTimeZone.FromOffset(PlusFiveThirty);

            (DateTime startUtc, DateTime endUtcExclusive) = tz.PeriodFromLocalDates(
                new DateTime(2026, 1, 10, 13, 45, 0), new DateTime(2026, 1, 11, 8, 30, 0));

            Assert.Equal(new DateTime(2026, 1, 9, 18, 30, 0, DateTimeKind.Utc), startUtc);
            Assert.Equal(new DateTime(2026, 1, 11, 18, 30, 0, DateTimeKind.Utc), endUtcExclusive);
        }

        [Fact]
        public void PeriodFromLocalDates_EndBeforeStart_Throws()
        {
            TargetTimeZone tz = TargetTimeZone.FromOffset(PlusFiveThirty);

            Assert.Throws<ArgumentException>(() =>
                tz.PeriodFromLocalDates(new DateTime(2026, 1, 11), new DateTime(2026, 1, 10)));
        }

        [Theory]
        [InlineData(0, "+00:00")]
        [InlineData(330, "+05:30")]
        [InlineData(-570, "-09:30")]
        [InlineData(840, "+14:00")]
        [InlineData(-30, "-00:30")]
        public void FormatOffset_SignedHoursMinutes(int minutes, string expected)
        {
            Assert.Equal(expected, TargetTimeZone.FormatOffset(TimeSpan.FromMinutes(minutes)));
        }

        [Theory]
        [InlineData(-840)]
        [InlineData(-30)]
        [InlineData(0)]
        [InlineData(345)]
        [InlineData(840)]
        public void FormatThenParse_RoundTrip(int minutes)
        {
            Assert.True(TargetTimeZone.TryParseOffset(TargetTimeZone.FormatOffset(TimeSpan.FromMinutes(minutes)), out TimeSpan parsed));
            Assert.Equal(TimeSpan.FromMinutes(minutes), parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("03:00")]
        [InlineData("+3:00")]
        [InlineData("+03:60")]
        [InlineData("+\uFF10\uFF13:00")]
        public void TryParseOffset_RejectsMalformed(string? text)
        {
            Assert.False(TargetTimeZone.TryParseOffset(text, out _));
        }

        [Fact]
        public void FormatOffset_OutOfRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TargetTimeZone.FormatOffset(TimeSpan.FromHours(25)));
        }

        [Fact]
        public void UtcToLocal_ExtremeValues_Saturate_NoOverflow()
        {
            // Аудит: у битого элемента «нет даты» (MaxValue/MinValue) — показ времени не должен падать.
            Assert.Equal(DateTime.MaxValue.Ticks, TargetTimeZone.FromHours(5).UtcToLocal(DateTime.MaxValue).Ticks);
            Assert.Equal(DateTime.MinValue.Ticks, TargetTimeZone.FromHours(-5).UtcToLocal(DateTime.MinValue).Ticks);
        }

        [Fact]
        public void PeriodFromLocalDates_ExtremeDate_ClearErrorNotOverflow()
        {
            TargetTimeZone tz = TargetTimeZone.FromHours(3);

            Assert.Throws<ArgumentOutOfRangeException>(() => tz.PeriodFromLocalDates(new DateTime(2026, 1, 1), DateTime.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => tz.PeriodFromLocalDates(DateTime.MinValue, new DateTime(2026, 1, 1)));
        }

        [Fact]
        public void OffsetText_MatchesFormat()
        {
            Assert.Equal("+05:30", TargetTimeZone.FromOffset(PlusFiveThirty).OffsetText);
        }

        [Fact]
        public void UtcToLocal_AddsOffset_AsUnspecified()
        {
            TargetTimeZone tz = TargetTimeZone.FromOffset(PlusFiveThirty);

            DateTime local = tz.UtcToLocal(new DateTime(2026, 1, 9, 18, 30, 0, DateTimeKind.Utc));

            Assert.Equal(new DateTime(2026, 1, 10, 0, 0, 0), local);
            Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        }
    }
}
