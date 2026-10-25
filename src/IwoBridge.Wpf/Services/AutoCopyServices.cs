using System;
using System.Diagnostics;
using System.Windows.Threading;
using IwoBridge.App.Configuration;
using Microsoft.Win32;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Однократный таймер с перевзводом — за портом, чтобы расписание автокопирования проверялось тестами без ожидания.
    /// Однократный намеренно: следующий запуск назначается после окончания текущего, и запуски не накладываются.
    /// </summary>
    public interface IOneShotTimer : IDisposable
    {
        /// <summary>Сработать один раз через <paramref name="due"/> (прежнее назначение отменяется).</summary>
        void Schedule(TimeSpan due);

        void Cancel();

        event EventHandler? Elapsed;
    }

    /// <summary>
    /// Таймер в потоке окна (<see cref="DispatcherTimer"/>): обработчик выполняется там же, где модели и привязки, без
    /// переходов между потоками. Остановленный таймер диспетчер не держит — утечки нет; Dispose останавливает его.
    /// </summary>
    public sealed class DispatcherOneShotTimer : IOneShotTimer
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Background);

        public DispatcherOneShotTimer()
        {
            _timer.Tick += OnTick;
        }

        public event EventHandler? Elapsed;

        public void Schedule(TimeSpan due)
        {
            _timer.Stop();
            _timer.Interval = due < TimeSpan.Zero ? TimeSpan.Zero : due;
            _timer.Start();
        }

        public void Cancel() => _timer.Stop();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            Elapsed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Запуск программы при входе в Windows (свёрнутой в область уведомлений).</summary>
    public interface IAutostart
    {
        bool IsEnabled { get; }

        void Set(bool enabled);
    }

    /// <summary>
    /// Автозапуск через раздел реестра текущего пользователя <c>HKCU\…\Run</c> — без прав администратора, как и вся
    /// программа. Программа переносная: если папку перенесли, путь обновляется при следующем запуске (<see cref="Refresh"/>).
    /// </summary>
    public sealed class RegistryAutostart : IAutostart
    {
        public const string TrayArgument = "--tray";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "iwo Calendar Bridge";

        private readonly string _command;

        public RegistryAutostart(string exePath)
        {
            _command = "\"" + exePath + "\" " + TrayArgument;
        }

        public bool IsEnabled
        {
            get
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key?.GetValue(ValueName) is string;
                }
            }
        }

        public void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    key.SetValue(ValueName, _command, RegistryValueKind.String);
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }

        /// <summary>
        /// Включён автозапуск, а программы по записанному пути больше нет (папку перенесли) — записать текущий путь.
        /// Другая существующая копия программы (например, проверочная) автозапуск себе не забирает: запуск копии из
        /// другой папки не должен молча менять, какая программа стартует с Windows.
        /// </summary>
        public void Refresh()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    if (key?.GetValue(ValueName) is string current
                        && !string.Equals(current, _command, StringComparison.OrdinalIgnoreCase)
                        && !System.IO.File.Exists(ExecutableOf(current)))
                    {
                        key.SetValue(ValueName, _command, RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.IO.IOException)
            {
                Trace.TraceWarning("Автозапуск не обновлён: " + ex.Message);
            }
        }

        /// <summary>Путь к программе из команды автозапуска: <c>"путь" --tray</c> (или путь без кавычек).</summary>
        internal static string ExecutableOf(string command)
        {
            string text = command.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : string.Empty;
            }

            int space = text.IndexOf(' ');
            return space < 0 ? text : text.Substring(0, space);
        }
    }

    /// <summary>Настройки копирования между запусками.</summary>
    public interface ICopySettingsStore
    {
        CopySettings Load();

        void Save(CopySettings settings);
    }

    /// <summary><c>config\copy.json</c> рядом с программой.</summary>
    public sealed class FileCopySettingsStore : ICopySettingsStore
    {
        private readonly string _path;
        private bool _readOk = true;

        public FileCopySettingsStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

        public CopySettings Load()
        {
            _readOk = CopySettingsFile.TryLoad(_path, out CopySettings settings);
            return settings;
        }

        /// <summary>Файл не прочитался при запуске (занят, нет прав) — он не перезаписывается: в нём настройки человека.</summary>
        public void Save(CopySettings settings)
        {
            if (!_readOk)
            {
                throw new System.IO.IOException("Файл настроек копирования не прочитался при запуске — он не перезаписывается, чтобы не потерять настройки.");
            }

            CopySettingsFile.Save(_path, settings);
        }
    }

    /// <summary>В памяти — демонстрационный режим и тесты.</summary>
    public sealed class MemoryCopySettingsStore : ICopySettingsStore
    {
        public MemoryCopySettingsStore(CopySettings? initial = null) => Current = initial ?? new CopySettings();

        public CopySettings Current { get; private set; }

        public int Saves { get; private set; }

        public CopySettings Load() => Current;

        public void Save(CopySettings settings)
        {
            Current = settings;
            Saves++;
        }
    }
}
