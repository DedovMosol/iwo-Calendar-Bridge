using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Журнал непредвиденных ошибок: <c>logs\crash.log</c> рядом с программой. Окно ошибки показывает пользователю
    /// короткий текст, а полный (тип, стек вызовов) — здесь, для администратора и разработчика.
    ///  - Пишется только текст исключения и время; паролей в исключениях нет. Адрес сервера или ящика в тексте
    ///    ошибки может быть — журнал лежит на машине пользователя и никуда не отправляется.
    ///  - Размер ограничен: больше 1 МБ — прежний файл переименовывается в <c>crash.old.log</c> (одна копия).
    ///  - Запись не должна сама стать причиной падения: любая ошибка ввода-вывода молча пропускается, тогда
    ///    возвращается <c>null</c> (папка программы может быть только для чтения).
    /// </summary>
    internal static class CrashLog
    {
        internal const long MaxBytes = 1024 * 1024;

        /// <returns>Путь к журналу или <c>null</c>, если записать не удалось.</returns>
        public static string? TryWrite(string folder, Exception exception, DateTime now)
        {
            if (exception is null)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "crash.log");
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string old = Path.Combine(folder, "crash.old.log");
                    File.Delete(old);
                    File.Move(path, old);
                }

                string entry = "==== " + now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + Environment.NewLine
                    + exception + Environment.NewLine + Environment.NewLine;
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(entry);
                }

                return path;
            }
#pragma warning disable CA1031 // Журнал — вспомогательный: его сбой не должен прятать исходную ошибку.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }
    }
}
