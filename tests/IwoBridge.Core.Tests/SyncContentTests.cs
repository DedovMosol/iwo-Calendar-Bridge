using System;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Отпечаток содержимого: меняется от любого переносимого поля, не меняется от того, что смысла не несёт
    /// (порядок вложений и исключений, вид переводов строк, пробелы в концах строк), версионирован.
    /// </summary>
    public sealed class SyncContentTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Hash_StableAndVersioned()
        {
            string hash = Basic().Hash;

            Assert.Equal(hash, Basic().Hash);
            Assert.StartsWith("v1:", hash);
            Assert.Equal(3 + 64, hash.Length);
        }

        [Fact]
        public void Hash_ChangesWithEveryField()
        {
            string basic = Basic().Hash;
            var attachment = new SyncAttachment("a.pdf", 10, "AB");

            Assert.NotEqual(basic, Make(subject: "Другая").Hash);
            Assert.NotEqual(basic, Make(location: "Зал 2").Hash);
            Assert.NotEqual(basic, Make(body: "Повестка изменена").Hash);
            Assert.NotEqual(basic, Make(start: Start.AddMinutes(30)).Hash);
            Assert.NotEqual(basic, Make(end: Start.AddHours(2)).Hash);
            Assert.NotEqual(basic, Make(allDay: true).Hash);
            Assert.NotEqual(basic, Make(busy: "Tentative").Hash);
            Assert.NotEqual(basic, new SyncContent("Планёрка", "Зал 1", "Повестка", Start, Start.AddHours(1), false, "Busy", sensitivity: "Private").Hash);
            Assert.NotEqual(basic, Make(recurrence: "FREQ=WEEKLY").Hash);
            Assert.NotEqual(basic, Make(exceptions: new[] { "2026-10-12:deleted" }).Hash);
            Assert.NotEqual(basic, Make(attachments: new[] { attachment }).Hash);
            Assert.NotEqual(Make(attachments: new[] { attachment }).Hash, Make(attachments: new[] { new SyncAttachment("a.pdf", 10, "AC") }).Hash);
        }

        [Fact]
        public void Hash_IgnoresOrderLineEndsAndTrailingSpaces()
        {
            var a = new SyncAttachment("a.pdf", 1, "aa");
            var b = new SyncAttachment("b.pdf", 2, "BB");

            Assert.Equal(
                Make(body: "Строка 1\r\nСтрока 2", attachments: new[] { a, b }, exceptions: new[] { "x", "y" }).Hash,
                Make(body: "Строка 1  \nСтрока 2\n\n", attachments: new[] { b, a }, exceptions: new[] { "y", "x" }).Hash);
        }

        /// <summary>
        /// Отпечаток события без категорий тот же, что до их переноса: иначе после обновления программы все копии
        /// перезаписались бы без изменений в источнике (а серии — пересоздались).
        /// </summary>
        [Fact]
        public void Hash_WithoutCategories_SameAsBefore()
        {
            DateTime original = new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc);
            var series = new SourceItem(
                "s", "Планёрка", "Зал", "Повестка", original.AddDays(-7), original.AddDays(-7).AddHours(1),
                recurrence: new RecurrenceRule(RecurrencePattern.Weekly, 1, original.AddDays(-7).Date, days: WeekDays.Monday),
                exceptions: new[]
                {
                    new SourceException(original, false, original.AddHours(1), original.AddHours(2), "Перенос", "Зал 2", "Текст"),
                    new SourceException(original.AddDays(7), true),
                    new SourceException(original.AddDays(14), false, original.AddDays(14), original.AddDays(14).AddHours(1), "Врач", "Поликлиника", null, isPrivate: true),
                });

            // Значения записаны версией 0.13.0 (до переноса категорий).
            Assert.Equal("v1:9514D70E5F9A0EB5DFC9BA2605A07347882A7B3C19104C273E00CF0FCEE6B09C", Basic().Hash);
            Assert.Equal("v1:ED133418689E0A828EA0273B47E83591EF944E4417A68A0460634AF8BCD9AB9A", series.ContentHash);
            Assert.Equal("v1:8BD4A15DE63617063643BFEC0E11ED5CB37A8629F775EE12EE8CA398E0F518F9", CopyRules.Prepare(series, PrivateEvents.HideDetails)!.ContentHash);
            Assert.Equal(series.ContentHash, WithCategories(series, Array.Empty<string>()).ContentHash);
        }

        [Fact]
        public void Hash_FieldBoundariesUnambiguous()
        {
            Assert.NotEqual(Make(subject: "ab", location: "c").Hash, Make(subject: "a", location: "bc").Hash);
        }

        [Fact]
        public void LocalTime_NormalizedToUtc()
        {
            DateTime local = Start.ToLocalTime();

            Assert.Equal(Basic().Hash, Make(start: local, end: local.AddHours(1)).Hash);
        }

        [Fact]
        public void Hash_Categories_ChangeIt_OrderAndRepeatsDoNot()
        {
            string none = Basic().Hash;
            string red = new SyncContent("Планёрка", "Зал 1", "Повестка", Start, Start.AddHours(1), false, "Busy", categories: new[] { "Красная категория" }).Hash;
            string both = new SyncContent("Планёрка", "Зал 1", "Повестка", Start, Start.AddHours(1), false, "Busy", categories: new[] { "Красная категория", "Проект" }).Hash;
            string reordered = new SyncContent(
                "Планёрка", "Зал 1", "Повестка", Start, Start.AddHours(1), false, "Busy", categories: new[] { " Проект ", "Красная категория", "проект", "" }).Hash;

            Assert.NotEqual(none, red);
            Assert.NotEqual(red, both);
            Assert.Equal(both, reordered);
        }

        /// <summary>То же событие с другими категориями (у серии — и у повторений, как прочитал источник).</summary>
        internal static SourceItem WithCategories(SourceItem item, string[] categories) => new SourceItem(
            item.SourceId, item.Subject, item.Location, item.Body, item.StartUtc, item.EndUtc, item.IsAllDay, item.Busy, item.Sensitivity, item.Recurrence,
            item.SeriesEndUtc, item.Exceptions, item.Attachments, item.RtfCompressed, categories);

        private static SyncContent Basic() => Make();

        private static SyncContent Make(
            string subject = "Планёрка",
            string location = "Зал 1",
            string body = "Повестка",
            DateTime? start = null,
            DateTime? end = null,
            bool allDay = false,
            string busy = "Busy",
            string? recurrence = null,
            string[]? exceptions = null,
            SyncAttachment[]? attachments = null)
        {
            DateTime s = start ?? Start;
            return new SyncContent(subject, location, body, s, end ?? Start.AddHours(1), allDay, busy, recurrence, exceptions, attachments);
        }
    }
}
