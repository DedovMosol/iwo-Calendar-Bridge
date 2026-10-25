using System;
using System.Linq;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Исключения серии: отпечаток различает любые правки полей (в том числе с «|» внутри и очищенное поле), а личное
    /// повторение обычной серии переносится по правилу для личных.
    /// </summary>
    public sealed class SourceExceptionRulesTests
    {
        private static readonly DateTime Original = new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);

        private static SourceException Modified(string? subject, string? location, string? body = null, bool isPrivate = false) =>
            new SourceException(Original, false, Original.AddHours(1), Original.AddHours(2), subject, location, body, isPrivate);

        private static SourceItem Series(params SourceException[] exceptions) => new SourceItem(
            "s", "Планёрка", "Зал", "Повестка", Original, Original.AddHours(1),
            recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Original.Date, days: WeekDays.Monday),
            exceptions: exceptions);

        [Fact]
        public void Canonical_FieldBoundariesAndEmptiness_Distinguished()
        {
            Assert.NotEqual(Modified("a|b", "c").Canonical, Modified("a", "b|c").Canonical);
            Assert.NotEqual(Modified(null, "c").Canonical, Modified(string.Empty, "c").Canonical);
            Assert.NotEqual(Modified("a", "c").Canonical, Modified("a", "c", isPrivate: true).Canonical);
            Assert.Equal(Modified("a", "c").Canonical, Modified("a", "c").Canonical);
        }

        [Fact]
        public void PrivateOccurrence_InNormalSeries_HiddenByDefault()
        {
            SourceItem prepared = CopyRules.Prepare(Series(Modified("Врач", "Поликлиника", "Анализы", isPrivate: true)), PrivateEvents.HideDetails)!;

            SourceException occurrence = prepared.Exceptions.Single();
            Assert.Equal(CopyRules.PrivateSubject, occurrence.Subject);
            Assert.Equal(string.Empty, occurrence.Location);
            Assert.Equal(string.Empty, occurrence.Body);
            Assert.True(occurrence.IsPrivate);
            Assert.Equal("Планёрка", prepared.Subject); // сама серия не личная — её содержимое переносится
        }

        [Fact]
        public void PrivateOccurrence_Skip_BecomesDeleted_CopyAll_Kept()
        {
            SourceItem series = Series(Modified("Врач", "Поликлиника", isPrivate: true), Modified("Перенос", "Зал 2"));

            SourceItem skipped = CopyRules.Prepare(series, PrivateEvents.Skip)!;
            SourceItem all = CopyRules.Prepare(series, PrivateEvents.CopyAll)!;

            Assert.Contains(skipped.Exceptions, e => e.IsDeleted);
            Assert.Contains(skipped.Exceptions, e => e.Subject == "Перенос");
            Assert.Same(series, all);
        }

        [Fact]
        public void NormalSeries_NoPrivateOccurrences_Unchanged()
        {
            SourceItem series = Series(Modified("Перенос", "Зал 2"));

            Assert.Same(series, CopyRules.Prepare(series, PrivateEvents.HideDetails));
        }
    }
}
