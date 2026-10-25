using System;
using System.Linq;
using IwoBridge.Core.Errors;
using IwoBridge.Core.Sync;
using IwoBridge.Ews.Mapping;
using IwoBridge.Ews.Transport;
using Microsoft.Exchange.WebServices.Data;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Преобразования для копирования без сервера: правило повторения в обе стороны для каждого вида, окончание серии,
    /// неподдерживаемые наборы дней; пояс записи (код, который знает Windows Server 2003, без летнего времени); время
    /// событий «на весь день»; какие ошибки останавливают копирование целиком.
    /// </summary>
    public sealed class EwsCopyMappingTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 5);

        public static TheoryData<RecurrenceRule> Rules() => new TheoryData<RecurrenceRule>
        {
            new RecurrenceRule(RecurrencePattern.Daily, 2, Start, occurrences: 10),
            new RecurrenceRule(RecurrencePattern.Weekly, 1, Start, days: WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday),
            new RecurrenceRule(RecurrencePattern.Weekly, 2, Start, endDate: Start.AddMonths(6), days: WeekDays.Tuesday),
            new RecurrenceRule(RecurrencePattern.MonthlyAbsolute, 1, Start, dayOfMonth: 15),
            new RecurrenceRule(RecurrencePattern.MonthlyRelative, 3, Start, days: WeekDays.Thursday, week: WeekOfMonth.Second),
            new RecurrenceRule(RecurrencePattern.MonthlyRelative, 1, Start, days: WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Friday, week: WeekOfMonth.Last),
            new RecurrenceRule(RecurrencePattern.YearlyAbsolute, 1, Start, dayOfMonth: 9, month: 5),
            new RecurrenceRule(RecurrencePattern.YearlyRelative, 1, Start, days: WeekDays.Sunday, week: WeekOfMonth.Last, month: 3),
        };

        [Theory]
        [MemberData(nameof(Rules))]
        public void Recurrence_RoundTrip(RecurrenceRule rule)
        {
            RecurrenceRule back = EwsRecurrenceMapper.FromEws(EwsRecurrenceMapper.ToEws(rule));

            Assert.Equal(rule.Canonical, back.Canonical);
        }

        [Fact]
        public void Recurrence_EndKinds()
        {
            Assert.False(EwsRecurrenceMapper.ToEws(new RecurrenceRule(RecurrencePattern.Daily, 1, Start)).HasEnd);
            Assert.Equal(5, EwsRecurrenceMapper.ToEws(new RecurrenceRule(RecurrencePattern.Daily, 1, Start, occurrences: 5)).NumberOfOccurrences);
            Assert.Equal(Start.AddDays(30), EwsRecurrenceMapper.ToEws(new RecurrenceRule(RecurrencePattern.Daily, 1, Start, endDate: Start.AddDays(30))).EndDate);
        }

        [Fact]
        public void Recurrence_RelativeWithArbitraryDays_RejectedNotDistorted()
        {
            var rule = new RecurrenceRule(RecurrencePattern.MonthlyRelative, 1, Start, days: WeekDays.Monday | WeekDays.Friday, week: WeekOfMonth.First);

            Assert.Throws<NotSupportedException>(() => EwsRecurrenceMapper.ToEws(rule));
        }

        [Theory]
        [InlineData(3, "Arab Standard Time")]
        [InlineData(4, "Arabian Standard Time")]
        [InlineData(0, "Greenwich Standard Time")]
        [InlineData(-5, "SA Pacific Standard Time")]
        public void CalendarZone_KnownToServer2003_NoDaylightSaving(int hours, string id)
        {
            TimeZoneInfo zone = CalendarZones.ForOffset(TimeSpan.FromHours(hours));

            Assert.Equal(id, zone.Id);
            Assert.Equal(TimeSpan.FromHours(hours), zone.BaseUtcOffset);
            Assert.False(zone.SupportsDaylightSavingTime);
            Assert.True(CalendarZones.IsExact(TimeSpan.FromHours(hours)));
        }

        [Fact]
        public void CalendarZone_WithoutClassicZone_FallsBackToUtc()
        {
            TimeZoneInfo zone = CalendarZones.ForOffset(TimeSpan.FromHours(-8));

            Assert.Equal(TimeSpan.Zero, zone.BaseUtcOffset);
            Assert.False(CalendarZones.IsExact(TimeSpan.FromHours(-8)));
        }

        [Fact]
        public void AllDay_WrittenAsLocalMidnights()
        {
            // Событие «на весь день» 6 октября в поясе +04:00: в UTC это 5 октября 20:00 — 6 октября 20:00.
            TimeZoneInfo zone = CalendarZones.ForOffset(TimeSpan.FromHours(4));
            DateTime startUtc = new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);

            (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(startUtc, startUtc.AddDays(1), isAllDay: true, zone);

            Assert.Equal(new DateTime(2026, 10, 6), start);
            Assert.Equal(new DateTime(2026, 10, 7), end);
            Assert.Equal(DateTimeKind.Unspecified, start.Kind);
        }

        [Theory]
        [InlineData(5)] // источник восточнее приёмника: полночь источника — 22:00 накануне в +03:00
        [InlineData(1)] // западнее: 02:00 того же дня
        [InlineData(3)] // тот же пояс
        public void AllDay_FromOtherZone_SameLocalDate(int sourceOffsetHours)
        {
            TimeZoneInfo zone = CalendarZones.ForOffset(TimeSpan.FromHours(3));
            DateTime startUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-sourceOffsetHours);
            (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(startUtc, startUtc.AddDays(2), isAllDay: true, zone);
            Assert.Equal(new DateTime(2026, 5, 1), start);
            Assert.Equal(new DateTime(2026, 5, 3), end);
        }

        [Fact]
        public void AllDay_NearMaxValue_NoOverflow()
        {
            DateTime startUtc = DateTime.SpecifyKind(DateTime.MaxValue.Date.AddHours(-1), DateTimeKind.Utc);
            (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(startUtc, startUtc, isAllDay: true, CalendarZones.ForOffset(TimeSpan.Zero));
            Assert.True(end > start);
        }

        [Fact]
        public void Timed_WrittenAsExactUtc()
        {
            DateTime startUtc = new DateTime(2026, 10, 5, 6, 30, 0, DateTimeKind.Utc);

            (DateTime start, DateTime end) = EwsFieldMapper.TimesForWrite(startUtc, startUtc.AddHours(1), isAllDay: false, CalendarZones.ForOffset(TimeSpan.FromHours(4)));

            Assert.Equal(startUtc, start);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
            Assert.Equal(startUtc.AddHours(1), end);
        }

        [Fact]
        public void BusyAndSensitivity_RoundTrip()
        {
            foreach (FreeBusy busy in Enum.GetValues(typeof(FreeBusy)).Cast<FreeBusy>())
            {
                Assert.Equal(busy, EwsFieldMapper.FromEws(EwsFieldMapper.ToEws(busy)));
            }

            foreach (ItemSensitivity sensitivity in Enum.GetValues(typeof(ItemSensitivity)).Cast<ItemSensitivity>())
            {
                Assert.Equal(sensitivity, EwsFieldMapper.FromEws(EwsFieldMapper.ToEws(sensitivity)));
            }
        }

        [Theory]
        [InlineData(OperationErrorKind.AuthenticationFailed, true)]
        [InlineData(OperationErrorKind.ConnectionReset, true)]
        [InlineData(OperationErrorKind.AccessDenied, true)]
        [InlineData(OperationErrorKind.CertificatePinMismatch, true)]
        [InlineData(OperationErrorKind.Unknown, false)]
        [InlineData(OperationErrorKind.SchemaError, false)]
        [InlineData(OperationErrorKind.RequestTooLarge, false)]
        public void StopsBatch_OnlyConnectionWideErrors(OperationErrorKind kind, bool stops)
        {
            Assert.Equal(stops, ErrorClassifier.StopsBatch(kind));
        }
    }
}
