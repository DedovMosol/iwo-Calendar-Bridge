using System;
using System.Collections.Generic;
using System.Linq;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Категории событий: названия переносятся как есть (без пустых и повторов), изменённое повторение хранит свои только
    /// если они отличаются от серии, личное — без категорий, подсказка предпросмотра — с цветом источника.
    /// </summary>
    public sealed class EventCategoriesTests
    {
        private static readonly DateTime Original = new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);

        private static SourceException Modified(IEnumerable<string>? categories = null, bool isPrivate = false) =>
            new SourceException(Original, false, Original.AddHours(1), Original.AddHours(2), "Перенос", "Зал 2", null, isPrivate, categories);

        private static SourceItem Series(string[] categories, params SourceException[] exceptions) => new SourceItem(
            "s", "Планёрка", "Зал", "Повестка", Original.AddDays(-7), Original.AddDays(-7).AddHours(1),
            recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Original.AddDays(-7).Date, days: WeekDays.Monday),
            exceptions: exceptions,
            categories: categories);

        [Fact]
        public void Normalize_TrimsDropsEmptyAndRepeats_KeepsOrder()
        {
            Assert.Equal(new[] { "Проект", "Красная категория" }, EventCategories.Normalize(new[] { " Проект", null, "", "Красная категория", "проект " }));
            Assert.Empty(EventCategories.Normalize(null));
        }

        [Fact]
        public void OfOccurrence_SameAsSeries_IsNull_OtherwiseOwn()
        {
            string[] series = { "Проект", "Красная категория" };

            Assert.Null(EventCategories.OfOccurrence(new[] { "Красная категория", "Проект" }, series));
            Assert.Equal(new[] { "Синяя категория" }, EventCategories.OfOccurrence(new[] { "Синяя категория" }, series));
            Assert.Empty(EventCategories.OfOccurrence(Array.Empty<string>(), series)!);
            Assert.Null(EventCategories.OfOccurrence(null, Array.Empty<string>()));
            Assert.Equal(new[] { "проект" }, EventCategories.OfOccurrence(new[] { "проект" }, new[] { "Проект" })); // смена регистра переносится
        }

        [Fact]
        public void Canonical_OwnCategories_Distinguished_InheritedUnchanged()
        {
            string inherited = Modified().Canonical;

            Assert.DoesNotContain("categories", inherited);
            Assert.NotEqual(inherited, Modified(Array.Empty<string>()).Canonical);
            Assert.NotEqual(Modified(new[] { "a|b" }).Canonical, Modified(new[] { "a", "b" }).Canonical);
            Assert.Equal(Modified(new[] { "a", "b" }).Canonical, Modified(new[] { "b", "a" }).Canonical);
        }

        [Fact]
        public void DeletedOccurrence_HasNoCategories()
        {
            Assert.Null(new SourceException(Original, true, categories: new[] { "Проект" }).Categories);
        }

        [Fact]
        public void ContentHash_FollowsCategories()
        {
            SourceItem plain = Series(Array.Empty<string>());
            SourceItem red = Series(new[] { "Красная категория" });
            SourceItem ownOccurrence = Series(new[] { "Красная категория" }, Modified(new[] { "Синяя категория" }));

            Assert.NotEqual(plain.ContentHash, red.ContentHash);
            Assert.NotEqual(Series(new[] { "Красная категория" }, Modified()).ContentHash, ownOccurrence.ContentHash);
            Assert.Equal(red.ContentHash, Series(new[] { "Красная категория" }).ContentHash);
        }

        [Fact]
        public void PrivateEvent_Hidden_LosesCategories_CopyAll_KeepsThem()
        {
            var item = new SourceItem(
                "p", "Врач", "Поликлиника", null, Original, Original.AddHours(1), sensitivity: ItemSensitivity.Private, categories: new[] { "Здоровье" });

            Assert.Empty(CopyRules.Prepare(item, PrivateEvents.HideDetails)!.Categories);
            Assert.Equal(new[] { "Здоровье" }, CopyRules.Prepare(item, PrivateEvents.CopyAll)!.Categories);
        }

        [Fact]
        public void PrivateSeries_Hidden_OccurrencesInheritEmpty()
        {
            var series = new SourceItem(
                "p", "Врач", null, null, Original.AddDays(-7), Original.AddDays(-7).AddHours(1), sensitivity: ItemSensitivity.Private,
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, Original.AddDays(-7).Date, days: WeekDays.Monday),
                exceptions: new[] { Modified(new[] { "Анализы" }) },
                categories: new[] { "Здоровье" });

            SourceItem hidden = CopyRules.Prepare(series, PrivateEvents.HideDetails)!;

            Assert.Empty(hidden.Categories);
            Assert.Null(hidden.Exceptions.Single().Categories);
        }

        /// <summary>
        /// Личное повторение обычной серии: его свои категории («Анализы») не переносятся, у скрытого повторения —
        /// категории серии: они и так видны на остальных повторениях, а снять их у одного повторения Exchange 2007 не даёт.
        /// </summary>
        [Fact]
        public void PrivateOccurrence_InNormalSeries_OwnCategoriesDropped_SeriesOnesInherited()
        {
            SourceItem prepared = CopyRules.Prepare(Series(new[] { "Проект" }, Modified(new[] { "Анализы" }, isPrivate: true)), PrivateEvents.HideDetails)!;

            Assert.Equal(new[] { "Проект" }, prepared.Categories);
            Assert.Null(prepared.Exceptions.Single().Categories);
            Assert.DoesNotContain(EventCategories.Used(new[] { prepared }, null), c => c.Name == "Анализы");
        }

        [Fact]
        public void Used_CollectsSeriesAndOccurrences_WithSourceColors()
        {
            var colors = new SourceRead(
                Array.Empty<SourceItem>(),
                categoryColors: new Dictionary<string, CategoryColor> { ["красная категория"] = CategoryColor.Red }).CategoryColors;
            SourceItem[] items =
            {
                Series(new[] { "Проект", "Красная категория" }, Modified(new[] { "Выезд" })),
                new SourceItem("x", "Звонок", null, null, Original, Original.AddHours(1), categories: new[] { "проект" }),
            };

            IReadOnlyList<EventCategory> used = EventCategories.Used(items, colors);

            Assert.Equal(new[] { "Выезд", "Красная категория", "Проект" }, used.Select(c => c.Name));
            Assert.Equal(new CategoryColor?[] { null, CategoryColor.Red, null }, used.Select(c => c.Color));
        }
    }
}
