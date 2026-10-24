using System;
using System.IO;
using IwoBridge.Wpf.Services;
using Xunit;

namespace IwoBridge.Wpf.Tests
{
    /// <summary>Журнал непредвиденных ошибок: дописывается, ограничен по размеру и сам никогда не падает.</summary>
    public sealed class CrashLogTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "iwo-crashlog-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }

        [Fact]
        public void Write_AppendsEntriesWithStack()
        {
            var at = new DateTime(2026, 10, 22, 12, 30, 0, DateTimeKind.Local);

            string? first = CrashLog.TryWrite(_folder, Thrown(new InvalidOperationException("первая")), at);
            string? second = CrashLog.TryWrite(_folder, new ArgumentException("вторая"), at);

            Assert.NotNull(first);
            Assert.Equal(first, second);
            string text = File.ReadAllText(first!);
            Assert.Contains("2026-10-22 12:30:00", text);
            Assert.Contains("первая", text);
            Assert.Contains("вторая", text);
            Assert.Contains(nameof(Thrown), text); // стек вызовов — для разработчика
        }

        [Fact]
        public void Write_LargeLog_KeepsOneOldCopy()
        {
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "crash.log");
            File.WriteAllText(path, new string('x', (int)CrashLog.MaxBytes + 10));
            File.WriteAllText(Path.Combine(_folder, "crash.old.log"), "совсем старое");

            CrashLog.TryWrite(_folder, new InvalidOperationException("новая"), DateTime.Now);

            Assert.True(new FileInfo(path).Length < 10_000);
            Assert.Contains("новая", File.ReadAllText(path));
            Assert.Equal(CrashLog.MaxBytes + 10, new FileInfo(Path.Combine(_folder, "crash.old.log")).Length);
        }

        [Fact]
        public void Write_UnusableFolder_ReturnsNull_DoesNotThrow()
        {
            Assert.Null(CrashLog.TryWrite("bad\0folder", new InvalidOperationException("x"), DateTime.Now));
            Assert.Null(CrashLog.TryWrite(_folder, null!, DateTime.Now));
        }

        private static Exception Thrown(Exception exception)
        {
            try
            {
                throw exception;
            }
            catch (Exception caught)
            {
                return caught;
            }
        }
    }
}
