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
