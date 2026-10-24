using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Time;

namespace IwoBridge.App.Cleanup
{
    /// <summary>
    /// Манифест удаляемого в CSV — для проверки человеком (Excel, Блокнот) и поиска удалённого в «Удалённых».
    ///
    /// Формат: UTF-8 с BOM (Excel распознаёт кириллицу), строки CRLF, каждое поле в кавычках с удвоением
    /// внутренних кавычек (RFC 4180). Разделитель — системный разделитель списков культуры (его же берёт
    /// Excel при открытии двойным щелчком), если это один из <c>, ; Tab |</c>; иначе <c>;</c>. Время — в поясе
    /// приёмника с явным смещением, <c>yyyy-MM-dd HH:mm zzz</c>; для серии целиком — время первого повторения.
    ///
    /// Темы приходят извне (приглашение может прислать кто угодно), поэтому поле, начинающееся с символа
    /// формулы (<c>= + - @</c>, их полноширинных вариантов, Tab, CR, LF — в том числе после пробелов),
    /// предваряется апострофом: таблица покажет текст, а не выполнит формулу (OWASP «CSV Injection»).
    ///
    /// Запись атомарна: временный файл → сброс на диск → переименование. Оборванная запись не оставляет
    /// файла, похожего на полный манифест. Имя — <c>cleanup-ГГГГММДД-ччммссZ-xxxxxxxx.csv</c> (UTC и
    /// случайный суффикс), существующие файлы не перезаписываются.
    /// </summary>
    public sealed class CsvCleanupManifestWriter : ICleanupManifestWriter
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm zzz";
        private const char FallbackDelimiter = ';';
        private const string SupportedDelimiters = ",;\t|";

        // Символы, с которых таблица начинает формулу, включая полноширинные ＝ ＋ － ＠ (OWASP).
        private const string FormulaTriggers = "=+-@\uFF1D\uFF0B\uFF0D\uFF20";

        private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        private readonly TargetTimeZone _timeZone;
        private readonly char _delimiter;

        /// <param name="directory">Папка манифестов (создаётся при первой записи); лучше абсолютный путь.</param>
        /// <param name="timeZone">Пояс приёмника — в нём показывается время.</param>
        /// <param name="culture">Культура для выбора разделителя; <c>null</c> — текущая культура пользователя.</param>
        public CsvCleanupManifestWriter(string directory, TargetTimeZone timeZone, CultureInfo? culture = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Нужна папка для манифестов.", nameof(directory));
            }

            DirectoryPath = Path.GetFullPath(directory);
            _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
            _delimiter = DelimiterFor(culture ?? CultureInfo.CurrentCulture);
        }

        /// <summary>Полный путь папки манифестов.</summary>
        public string DirectoryPath { get; }

        /// <summary>Разделитель для культуры: её разделитель списков, если он поддержан, иначе <c>;</c>.</summary>
        public static char DelimiterFor(CultureInfo culture)
        {
            if (culture is null)
            {
                throw new ArgumentNullException(nameof(culture));
            }

            string separator = culture.TextInfo.ListSeparator;
            return separator.Length == 1 && SupportedDelimiters.IndexOf(separator[0]) >= 0
                ? separator[0]
                : FallbackDelimiter;
        }

        public async Task<string> WriteAsync(IReadOnlyList<CleanupManifestEntry> entries, CancellationToken cancellationToken)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            foreach (CleanupManifestEntry entry in entries)
            {
                if (entry is null)
                {
                    throw new ArgumentException("Манифест содержит null-строку.", nameof(entries));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);

            string finalPath = Path.Combine(DirectoryPath, NewFileName(DateTime.UtcNow));
            string tempPath = finalPath + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                using (var writer = new StreamWriter(stream, Utf8WithBom))
                {
                    writer.NewLine = "\r\n";
                    await writer.WriteLineAsync(Row("Тип", "Тема", "Начало", "Конец", "Идентификатор")).ConfigureAwait(false);
                    foreach (CleanupManifestEntry entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(Row(
                            CleanupLabels.Of(entry.Kind),
                            entry.Subject,
                            FormatTime(entry.StartUtc),
                            FormatTime(entry.EndUtc),
                            entry.ItemId)).ConfigureAwait(false);
                    }

                    await writer.FlushAsync().ConfigureAwait(false);

                    // До удаления манифест должен быть на диске, а не в кэше ОС: иначе сбой питания посреди
                    // удаления оставил бы удалённое без записи о нём.
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, finalPath);
                return finalPath;
            }
            finally
            {
                FileSafety.DeleteQuietly(tempPath);
            }
        }

        private static string NewFileName(DateTime utcNow)
        {
            string stamp = utcNow.ToString("yyyyMMdd'-'HHmmss'Z'", CultureInfo.InvariantCulture);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            return "cleanup-" + stamp + "-" + suffix + ".csv";
        }

        private static string Field(string? value)
        {
            string text = value ?? string.Empty;
            if (StartsLikeFormula(text))
            {
                text = "'" + text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static bool StartsLikeFormula(string text)
        {
            foreach (char c in text)
            {
                if (c == '\t' || c == '\r' || c == '\n')
                {
                    return true;
                }

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                return FormulaTriggers.IndexOf(c) >= 0;
            }

            return false;
        }

        private string Row(params string?[] fields)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(_delimiter);
                }

                builder.Append(Field(fields[i]));
            }

            return builder.ToString();
        }

        private string FormatTime(DateTime utc)
        {
            return new DateTimeOffset(_timeZone.UtcToLocal(utc), _timeZone.Offset)
                .ToString(TimeFormat, CultureInfo.InvariantCulture);
        }
    }
}
