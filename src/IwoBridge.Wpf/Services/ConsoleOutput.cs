using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Вывод в консоль для окна Windows. Своей консоли у оконной программы нет. Куда писать, решает то, как её запустили:
    /// <list type="bullet">
    /// <item>вывод перенаправлен в файл (cmd: <c>&gt; run.log</c>) — в файл, UTF-8;</item>
    /// <item>вывод перенаправлен в канал (PowerShell: <c>&gt; run.log</c>, <c>| Tee-Object</c>) — в канал, в кодовой
    /// странице консоли, которой PowerShell этот канал читает;</item>
    /// <item>вывод перенаправлен в <c>nul</c> — вывода нет;</item>
    /// <item>запуск из консоли без перенаправления — в саму консоль;</item>
    /// <item>двойной щелчок, автозапуск — вывода нет.</item>
    /// </list>
    /// Строки пишет фоновый поток (<see cref="QueuedWriter"/>): запись может ждать, а поток окон ждать не должен.
    /// </summary>
    public static class ConsoleOutput
    {
        private const int AttachParentProcess = -1;
        private const int StdOutputHandle = -11;
        private const int FileTypeDisk = 1;
        private const int FileTypeChar = 2;
        private const int FileTypePipe = 3;
        private const int Utf8CodePage = 65001;

        /// <summary>Писатель в консоль или перенаправленный вывод; <c>null</c> — выводить некуда. Закрыть при выходе.</summary>
        public static TextWriter? TryOpen()
        {
            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                int type = handle == IntPtr.Zero || handle == new IntPtr(-1) ? 0 : GetFileType(handle);
                if (type == FileTypeDisk || type == FileTypePipe)
                {
                    // Поток берётся до знакомства с консолью: он держит дескриптор перенаправления, а не консоли.
                    Stream output = Console.OpenStandardOutput();
                    Encoding encoding = type == FileTypeDisk ? new UTF8Encoding(false) : PipeEncoding();
                    return new QueuedWriter(new StreamWriter(output, encoding) { AutoFlush = true });
                }

                if (type == FileTypeChar && !IsParentConsole(handle))
                {
                    return null; // > nul: символьное устройство, но не консоль
                }

                ParentConsoleWriter? console = ParentConsoleWriter.TryCreate();
                return console is null ? null : new QueuedWriter(console);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Символьный дескриптор — консоль запустившего процесса (а не <c>nul</c>)?</summary>
        private static bool IsParentConsole(IntPtr handle)
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return false;
            }

            try
            {
                return GetConsoleMode(handle, out _);
            }
            finally
            {
                FreeConsole();
            }
        }

        /// <summary>
        /// Кодировка канала — та, которой его читает консоль запустившей программы (PowerShell декодирует вывод по
        /// <c>[Console]::OutputEncoding</c>, то есть по кодовой странице консоли). Консоли нет — OEM-страница системы.
        /// </summary>
        private static Encoding PipeEncoding()
        {
            int codePage = 0;
            if (AttachConsole(AttachParentProcess))
            {
                try
                {
                    codePage = GetConsoleOutputCP();
                }
                finally
                {
                    FreeConsole();
                }
            }

            if (codePage == 0)
            {
                codePage = GetOEMCP();
            }

            return codePage == Utf8CodePage ? new UTF8Encoding(false) : Encoding.GetEncoding(codePage);
        }

        /// <summary>
        /// Строки — в очередь, пишет их фоновый поток. Запись в консоль стоит, пока в окне консоли выделяют текст, запись
        /// в канал — пока его не читают. Поток окон, область уведомлений и расписание при этом не ждут. Очередь
        /// ограничена: вывод стоит долго — новые строки отбрасываются, а не копятся в памяти.
        /// </summary>
        internal sealed class QueuedWriter : TextWriter
        {
            private const int Capacity = 1000;
            private static readonly TimeSpan DrainOnClose = TimeSpan.FromSeconds(2);

            private readonly TextWriter _inner;
            private readonly BlockingCollection<string> _lines = new BlockingCollection<string>(Capacity);
            private readonly StringBuilder _pending = new StringBuilder();
            private readonly object _sync = new object();
            private readonly Thread _thread;

            public QueuedWriter(TextWriter inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _thread = new Thread(Drain) { IsBackground = true, Name = "Console output" };
                _thread.Start();
            }

            public override Encoding Encoding => _inner.Encoding;

            public override void Write(char value)
            {
                lock (_sync)
                {
                    _pending.Append(value);
                }
            }

            public override void Write(string? value)
            {
                lock (_sync)
                {
                    _pending.Append(value);
                }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (_sync)
                {
                    _pending.Append(buffer, index, count);
                }
            }

            /// <summary>Накопленное — в очередь (без ожидания). Очередь полна или закрыта — текст отбрасывается.</summary>
            public override void Flush()
            {
                string text;
                lock (_sync)
                {
                    if (_pending.Length == 0)
                    {
                        return;
                    }

                    text = _pending.ToString();
                    _pending.Clear();
                }

                try
                {
                    _lines.TryAdd(text);
                }
                catch (InvalidOperationException)
                {
                    // Писатель закрыт (выход из программы): строка опоздала.
                }
            }

            /// <summary>Выход: дописать очередь, ждать не дольше <see cref="DrainOnClose"/> (вывод может стоять).</summary>
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Flush();
                    _lines.CompleteAdding();
                    if (_thread.Join(DrainOnClose))
                    {
                        _lines.Dispose();
                        _inner.Dispose();
                    }
                }

                base.Dispose(disposing);
            }

            private void Drain()
            {
                foreach (string text in _lines.GetConsumingEnumerable())
                {
                    try
                    {
                        _inner.Write(text);
                        _inner.Flush();
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                        // Консоль закрыли или канал оборвался — программа работает дальше без вывода.
                    }
                }
            }
        }

        /// <summary>
        /// Строки в консоль, из которой запустили программу. Программа присоединяется к консоли только на время записи
        /// (<c>AttachConsole</c> — <c>WriteConsoleW</c> — <c>FreeConsole</c>): присоединённый процесс получает сигналы
        /// консоли, и её закрытие завершило бы программу посреди копирования. Ctrl+C и Ctrl+Break, пришедшие во время
        /// записи, программа поглощает. Текст пишется в Unicode. Консоль закрыли — строки пропадают молча.
        /// </summary>
        private sealed class ParentConsoleWriter : TextWriter
        {
            private const int CtrlC = 0;
            private const int CtrlBreak = 1;

            // CONOUT$ — экран консоли, к которой процесс присоединён (CreateFile, раздел «Consoles»): чтение и запись,
            // общий доступ на чтение и запись.
            private const uint GenericReadWrite = 0x80000000 | 0x40000000;
            private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
            private const uint OpenExisting = 3;

            // Делегат держится статическим полем: система вызывает его из своего потока в любой момент.
            private static readonly ConsoleCtrlHandler IgnoreInterrupts = type => type == CtrlC || type == CtrlBreak;
            private static int _handlerSet;

            private readonly StringBuilder _pending = new StringBuilder();
            private bool _started;

            private ParentConsoleWriter()
            {
            }

            private delegate bool ConsoleCtrlHandler(int ctrlType);

            public override Encoding Encoding => Encoding.Unicode;

            /// <summary>Есть ли консоль у запустившего процесса (cmd, PowerShell); нет — писать некуда.</summary>
            public static ParentConsoleWriter? TryCreate()
            {
                if (!AttachConsole(AttachParentProcess))
                {
                    return null;
                }

                FreeConsole();
                if (Interlocked.Exchange(ref _handlerSet, 1) == 0)
                {
                    SetConsoleCtrlHandler(IgnoreInterrupts, true);
                }

                return new ParentConsoleWriter();
            }

            public override void Write(char value) => _pending.Append(value);

            public override void Write(string? value) => _pending.Append(value);

            public override void Write(char[] buffer, int index, int count) => _pending.Append(buffer, index, count);

            public override void Flush()
            {
                if (_pending.Length == 0)
                {
                    return;
                }

                // Первая строка — с новой строки: приглашение консоли уже напечатано (cmd не ждёт оконную программу).
                string text = (_started ? string.Empty : Environment.NewLine) + _pending;
                _pending.Clear();
                if (!AttachConsole(AttachParentProcess))
                {
                    return;
                }

                try
                {
                    using (SafeFileHandle console = CreateFileW("CONOUT$", GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                    {
                        if (!console.IsInvalid && WriteConsoleW(console, text, text.Length, out _, IntPtr.Zero))
                        {
                            _started = true;
                        }
                    }
                }
                finally
                {
                    FreeConsole();
                }
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll")]
        private static extern int GetFileType(IntPtr handle);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr handle, out int mode);

        [DllImport("kernel32.dll")]
        private static extern int GetConsoleOutputCP();

        [DllImport("kernel32.dll")]
        private static extern int GetOEMCP();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteConsoleW(SafeFileHandle console, string text, int length, out int written, IntPtr reserved);
    }
}
