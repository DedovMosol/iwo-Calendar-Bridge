using System;
using System.IO;
using System.Linq;
using System.Text;
using IwoBridge.App.History;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Журнал операций в файле JSON Lines: запись и чтение всех полей, новые первыми, перенос в архивный файл по размеру,
    /// повреждённая строка не мешает остальным, сбой файла не роняет операцию; копия в консоль.
    /// </summary>
    public sealed class OperationLogTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "iwo-history-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private string LogPath => Path.Combine(_directory, "logs", "history.jsonl");

        private static OperationRecord Record(int n, OperationKind kind = OperationKind.Copy, OperationOutcome outcome = OperationOutcome.Success) =>
            new OperationRecord(
                kind,
                new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc).AddMinutes(n),
                new DateTime(2026, 10, 25, 10, 0, 12, DateTimeKind.Utc).AddMinutes(n),
                outcome,
                kind == OperationKind.Cleanup ? null : "Outlook: Календарь — box",
                "box@example.test на mail.example.test",
                "01.10.2026 – 31.12.2026",
                n,
                2,
                1,
                outcome == OperationOutcome.Problem ? 3 : 0,
                "Готово — «итог» с кавычками и \\ слэшем",
                new[] { "Событие «Планёрка» — сервер отклонил", "строка 2" });

        [Fact]
        public void Record_ThenRecent_AllFieldsRoundTrip_NewestFirst()
        {
            var log = new JsonLinesOperationLog(LogPath);
            OperationRecord? raised = null;
            log.Recorded += (_, r) => raised = r;

            log.Record(Record(1));
            log.Record(Record(2, OperationKind.Cleanup, OperationOutcome.Problem));

            OperationRecord[] read = log.Recent(10).ToArray();
            Assert.Equal(2, read.Length);
            OperationRecord last = read[0];
            Assert.Equal(OperationKind.Cleanup, last.Kind);
            Assert.Equal(OperationOutcome.Problem, last.Outcome);
            Assert.Null(last.Source);
            Assert.Equal(new DateTime(2026, 10, 25, 10, 2, 0, DateTimeKind.Utc), last.StartedUtc);
            Assert.Equal(DateTimeKind.Utc, last.StartedUtc.Kind);
            Assert.Equal(TimeSpan.FromSeconds(12), last.Duration);
            Assert.Equal(3, last.Failed);
            Assert.Equal("Готово — «итог» с кавычками и \\ слэшем", last.Title);
            Assert.Equal(new[] { "Событие «Планёрка» — сервер отклонил", "строка 2" }, last.Details);
            Assert.Equal("Outlook: Календарь — box", read[1].Source);
            Assert.Same(raised, read.Length > 0 ? raised : null);
            Assert.Single(log.Recent(1));
        }

        [Fact]
        public void File_IsJsonLines_Utf8WithoutBom_IsoUtcTimes()
        {
            var log = new JsonLinesOperationLog(LogPath);
            log.Record(Record(1));
            log.Record(Record(2));

            byte[] bytes = File.ReadAllBytes(LogPath);
            Assert.NotEqual(0xEF, bytes[0]);
            string[] lines = Encoding.UTF8.GetString(bytes).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.StartsWith("{", l, StringComparison.Ordinal));
            Assert.Contains("\"started\":\"2026-10-25T10:01:00.0000000Z\"", lines[0]);
            Assert.Contains("\"kind\":\"copy\"", lines[0]);
        }

        [Fact]
        public void Rotation_KeepsPreviousFile_ReadsBoth_NoMoreThanTwoFiles()
        {
            var log = new JsonLinesOperationLog(LogPath, maxBytes: 1500);
            for (int i = 1; i <= 12; i++)
            {
                log.Record(Record(i));
            }

            string[] files = Directory.GetFiles(Path.GetDirectoryName(LogPath)!);
            Assert.Equal(2, files.Length);
            Assert.True(new FileInfo(LogPath).Length <= 1500);
            OperationRecord[] read = log.Recent(100).ToArray();
            Assert.Equal(12, read[0].Created);
            Assert.True(read.Length >= 2 && read.Length < 12); // самые старые ушли вместе с удалённым архивом
            Assert.Equal(read.Select(r => r.Created).OrderByDescending(c => c), read.Select(r => r.Created));
        }

        [Fact]
        public void CorruptOrForeignLines_Skipped()
        {
            var log = new JsonLinesOperationLog(LogPath);
            log.Record(Record(1));
            File.AppendAllText(LogPath, "{\"kind\":\"copy\",\"started\":\"оборвано\n", new UTF8Encoding(false));
            File.AppendAllText(LogPath, "не json\n{\"v\":1}\n", new UTF8Encoding(false));
            log.Record(Record(2));

            Assert.Equal(new[] { 2, 1 }, log.Recent(10).Select(r => r.Created));
        }

        [Fact]
        public void FileUnavailable_RecordDoesNotThrow_EventStillRaised()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var log = new JsonLinesOperationLog(LogPath);
            bool raised = false;
            log.Recorded += (_, __) => raised = true;
            using (new FileStream(LogPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                log.Record(Record(1));
                Assert.Empty(log.Recent(10));
            }

            Assert.True(raised);
        }

        [Theory]
        [InlineData("{\"v\":1,\"kind\":\"co")] // запись оборвалась на полуслове
        [InlineData("\0\0\0\0")] // хвост из нулей после сбоя питания (NTFS)
        public void TornLastLine_NextRecordStartsOnNewLine_NotLost(string tail)
        {
            var log = new JsonLinesOperationLog(LogPath);
            log.Record(Record(1));
            File.AppendAllText(LogPath, tail);

            log.Record(Record(2));

            Assert.Equal(new[] { 2, 1 }, log.Recent(10).Select(r => r.Created));
        }

        [Fact]
        public void Mirrored_WritesLineAndProblemDetailsToConsole_StoresInInner()
        {
            var inner = new MemoryOperationLog();
            var console = new StringWriter();
            var log = new MirroredOperationLog(inner, console);

            log.Info("Копирование начато");
            log.Record(Record(4, outcome: OperationOutcome.Problem));

            // Знаки, которых нет в кодовой странице 866 и в растровом шрифте консоли Windows 7, — ASCII-заменами.
            string text = console.ToString();
            Assert.Contains("Копирование начато", text);
            Assert.Contains("Копирование  Outlook: Календарь - box -> box@example.test на mail.example.test", text);
            Assert.Contains("создано 4, перезаписано 2, удалено 1, ошибок 3", text);
            Assert.Contains("- есть проблемы (12 с)", text);
            Assert.Contains("    Событие \"Планёрка\" - сервер отклонил", text);
            Assert.DoesNotContain("→", text);
            Assert.DoesNotContain("—", text);
            Assert.True(text.All(c => c < 0x2000), "в выводе остались типографские знаки");
            Assert.Single(inner.Recent(10));
            Assert.Equal(new[] { "Копирование начато" }, inner.Messages);
        }

        [Theory]
        [InlineData(0.4, "0 с")]
        [InlineData(59, "59 с")]
        [InlineData(65, "1 мин 05 с")]
        [InlineData(3725, "1 ч 02 мин")]
        public void Duration_Readable(double seconds, string expected)
        {
            Assert.Equal(expected, OperationRecordText.Duration(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void Record_DetailsCapped_FinishBeforeStartClamped()
        {
            var record = new OperationRecord(
                OperationKind.Copy, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
                OperationOutcome.Success, null, "t", "", -1, 0, 0, 0, "x", Enumerable.Range(0, 200).Select(i => "d" + i));

            Assert.Equal(OperationRecord.MaxDetails, record.Details.Count);
            Assert.Equal(TimeSpan.Zero, record.Duration);
            Assert.Equal(0, record.Created);
        }
    }
}
