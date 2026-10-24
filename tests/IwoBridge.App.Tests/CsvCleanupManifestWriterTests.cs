using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Time;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Файл манифеста: кодировка и переводы строк, кавычки по RFC 4180, защита от формул (OWASP), время в поясе
    /// приёмника, атомарная запись. Каждый тест пишет во временную папку и удаляет её за собой.
    /// </summary>
    public sealed class CsvCleanupManifestWriterTests : IDisposable
    {
        private static readonly TargetTimeZone PlusFiveThirty = TargetTimeZone.FromOffset(new TimeSpan(5, 30, 0));
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "iwo-manifest-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static DateTime Utc(int day, int hour, int minute = 0) => new DateTime(2026, 1, day, hour, minute, 0, DateTimeKind.Utc);

        private static CleanupManifestEntry Entry(string subject, string id = "id-1") =>
            new CleanupManifestEntry(CleanupManifestEntryKind.Single, id, subject, Utc(15, 9), Utc(15, 10));

        private CsvCleanupManifestWriter Writer(TargetTimeZone? zone = null, CultureInfo? culture = null) =>
            new CsvCleanupManifestWriter(_directory, zone ?? PlusFiveThirty, culture ?? Russian);

        private async Task<IReadOnlyList<string[]>> WriteAndParseAsync(params CleanupManifestEntry[] entries)
        {
            string path = await Writer().WriteAsync(entries, CancellationToken.None);
            return CsvReader.Parse(File.ReadAllText(path, Encoding.UTF8), ';');
        }

        [Fact]
        public async Task Write_Utf8Bom_Crlf_HeaderAndQuotedFields()
        {
            string path = await Writer().WriteAsync(new[] { Entry("Планёрка") }, CancellationToken.None);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

            string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Equal(
                "\"Тип\";\"Тема\";\"Начало\";\"Конец\";\"Идентификатор\"\r\n"
                + "\"Событие\";\"Планёрка\";\"2026-01-15 14:30 +05:30\";\"2026-01-15 15:30 +05:30\";\"id-1\"\r\n",
                text);
        }

        [Fact]
        public async Task Write_NegativeOffset_CrossesMidnightBackwards()
        {
            var entry = new CleanupManifestEntry(
                CleanupManifestEntryKind.SeriesInstance, "occ-1", "Утро", Utc(15, 2), Utc(15, 3));
            string path = await Writer(TargetTimeZone.FromHours(-5)).WriteAsync(new[] { entry }, CancellationToken.None);

            string[] row = CsvReader.Parse(File.ReadAllText(path, Encoding.UTF8), ';')[1];
            Assert.Equal(new[] { "Экземпляр серии", "Утро", "2026-01-14 21:00 -05:00", "2026-01-14 22:00 -05:00", "occ-1" }, row);
        }

        [Fact]
        public async Task Write_KindLabels()
        {
            var entries = new[]
            {
                new CleanupManifestEntry(CleanupManifestEntryKind.Single, "a", null, Utc(15, 9), Utc(15, 10)),
                new CleanupManifestEntry(CleanupManifestEntryKind.WholeSeries, "b", null, Utc(15, 9), Utc(15, 10)),
                new CleanupManifestEntry(CleanupManifestEntryKind.SeriesInstance, "c", null, Utc(15, 9), Utc(15, 10)),
            };

            IReadOnlyList<string[]> rows = await WriteAndParseAsync(entries);

            Assert.Equal(new[] { "Событие", "Серия целиком", "Экземпляр серии" }, rows.Skip(1).Select(r => r[0]));
            Assert.All(rows.Skip(1), r => Assert.Equal(string.Empty, r[1]));
        }

        [Theory]
        [InlineData("=1+2")]
        [InlineData("+7 900 000-00-00")]
        [InlineData("-итоги")]
        [InlineData("@user")]
        [InlineData("\t=1+2")]
        [InlineData("\r\nx")]
        [InlineData("\nx")]
        [InlineData("  =HYPERLINK(\"x\")")]
        [InlineData("\uFF1D1+2")]
        [InlineData("\uFF0B1")]
        [InlineData("\uFF0D1")]
        [InlineData("\uFF20x")]
        public async Task Write_FormulaLikeSubject_PrefixedWithApostrophe(string subject)
        {
            IReadOnlyList<string[]> rows = await WriteAndParseAsync(Entry(subject));

            Assert.Equal("'" + subject, rows[1][1]);
        }

        [Theory]
        [InlineData("Планёрка")]
        [InlineData("Встреча = обсуждение")]
        [InlineData("1+1")]
        [InlineData("")]
        public async Task Write_OrdinarySubject_Unchanged(string subject)
        {
            IReadOnlyList<string[]> rows = await WriteAndParseAsync(Entry(subject));

            Assert.Equal(subject, rows[1][1]);
        }

        [Fact]
        public async Task Write_DelimitersQuotesAndLineBreaksInSubject_StayInOneField()
        {
            const string subject = "Отчёт; \"квартал\", итоги\r\nвторая строка; =1+2";

            IReadOnlyList<string[]> rows = await WriteAndParseAsync(Entry(subject));

            Assert.Equal(2, rows.Count);
            Assert.Equal(5, rows[1].Length);
            Assert.Equal(subject, rows[1][1]);
        }

        [Fact]
        public async Task Write_FileName_UtcStampAndRandomSuffix_NoTempLeft()
        {
            string first = await Writer().WriteAsync(new[] { Entry("a") }, CancellationToken.None);
            string second = await Writer().WriteAsync(new[] { Entry("b") }, CancellationToken.None);

            var pattern = new Regex(@"^cleanup-\d{8}-\d{6}Z-[0-9a-f]{8}\.csv$");
            Assert.Matches(pattern, Path.GetFileName(first));
            Assert.Matches(pattern, Path.GetFileName(second));
            Assert.NotEqual(first, second);
            Assert.Equal(Path.GetFullPath(_directory), Path.GetDirectoryName(first));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
            Assert.Equal(2, Directory.GetFiles(_directory, "*.csv").Length);
        }

        [Fact]
        public async Task Write_DirectoryMissing_IsCreated()
        {
            Assert.False(Directory.Exists(_directory));

            string path = await Writer().WriteAsync(new[] { Entry("a") }, CancellationToken.None);

            Assert.True(File.Exists(path));
        }

        [Fact]
        public async Task Write_PathOccupiedByFile_IOException_NoManifest()
        {
            // Сбой среды должен приходить как IOException — именно его сценарий превращает в отказ без удаления.
            Directory.CreateDirectory(_directory);
            string occupied = Path.Combine(_directory, "manifests");
            File.WriteAllText(occupied, "not a directory");
            var writer = new CsvCleanupManifestWriter(occupied, PlusFiveThirty, Russian);

            await Assert.ThrowsAnyAsync<IOException>(() => writer.WriteAsync(new[] { Entry("a") }, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(_directory, "*.csv"));
        }

        [Fact]
        public async Task Write_Cancelled_NothingWritten()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => Writer().WriteAsync(new[] { Entry("a") }, cts.Token));
            }

            Assert.False(Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any());
        }

        [Fact]
        public async Task Write_NullEntry_Throws_BeforeCreatingFile()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => Writer().WriteAsync(new CleanupManifestEntry[] { null! }, CancellationToken.None));

            Assert.False(Directory.Exists(_directory));
        }

        [Fact]
        public async Task Write_CommaCulture_UsesComma()
        {
            string path = await Writer(culture: CultureInfo.GetCultureInfo("en-US")).WriteAsync(
                new[] { Entry("a; b") }, CancellationToken.None);

            string[] row = CsvReader.Parse(File.ReadAllText(path, Encoding.UTF8), ',')[1];
            Assert.Equal("a; b", row[1]);
        }

        [Theory]
        [InlineData("ru-RU", ';')]
        [InlineData("en-US", ',')]
        [InlineData("de-DE", ';')]
        public void DelimiterFor_UsesCultureListSeparator(string cultureName, char expected)
        {
            Assert.Equal(expected, CsvCleanupManifestWriter.DelimiterFor(CultureInfo.GetCultureInfo(cultureName)));
        }

        [Theory]
        [InlineData("::")]
        [InlineData("#")]
        [InlineData("")]
        public void DelimiterFor_UnsupportedSeparator_FallsBackToSemicolon(string separator)
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.TextInfo.ListSeparator = separator;

            Assert.Equal(';', CsvCleanupManifestWriter.DelimiterFor(culture));
        }

        [Fact]
        public void Constructor_BlankDirectory_Throws()
        {
            Assert.Throws<ArgumentException>(() => new CsvCleanupManifestWriter("  ", PlusFiveThirty, Russian));
        }

        /// <summary>Минимальный разбор CSV по RFC 4180 — чтобы проверять, что прочтёт таблица, а не строки.</summary>
        private static class CsvReader
        {
            public static IReadOnlyList<string[]> Parse(string text, char delimiter)
            {
                var rows = new List<string[]>();
                var fields = new List<string>();
                var field = new StringBuilder();
                bool quoted = false;

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (quoted)
                    {
                        if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else if (c == '"')
                        {
                            quoted = false;
                        }
                        else
                        {
                            field.Append(c);
                        }
                    }
                    else if (c == '"')
                    {
                        quoted = true;
                    }
                    else if (c == delimiter)
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                    }
                    else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                        rows.Add(fields.ToArray());
                        fields.Clear();
                        i++;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }

                Assert.False(quoted, "Незакрытая кавычка в CSV.");
                Assert.True(field.Length == 0 && fields.Count == 0, "Последняя строка CSV без CRLF.");
                return rows;
            }
        }
    }
}
