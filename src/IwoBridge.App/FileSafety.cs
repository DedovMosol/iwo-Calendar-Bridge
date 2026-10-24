using System;
using System.IO;

namespace IwoBridge.App
{
    /// <summary>
    /// Безопасная работа с файлами настроек и манифестов — одно место (DRY): атомарная замена и тихое удаление
    /// временного файла.
    /// </summary>
    internal static class FileSafety
    {
        /// <summary>
        /// Ставит готовый временный файл на место целевого. Если целевой есть — <see cref="File.Replace(string, string, string)"/>
        /// (атомарно на NTFS), иначе <see cref="File.Move(string, string)"/>.
        ///
        /// Ловушка Win32 ReplaceFile: при ERROR_UNABLE_TO_MOVE_REPLACEMENT (например, антивирус держит файл) прежний
        /// файл уже удалён, а новый остался под временным именем. Тогда временный файл переносится на место —
        /// иначе пропали бы и старые, и новые данные.
        /// </summary>
        public static void ReplaceWith(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            }
            catch (IOException) when (!File.Exists(targetPath) && File.Exists(tempPath))
            {
                File.Move(tempPath, targetPath);
            }
        }

        /// <summary>Удаляет временный файл, если он остался; сбой удаления не маскирует исходную ошибку.</summary>
        public static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
