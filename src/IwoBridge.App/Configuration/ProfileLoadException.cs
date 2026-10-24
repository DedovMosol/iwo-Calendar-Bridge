using System;

namespace IwoBridge.App.Configuration
{
    /// <summary>
    /// Профиль подключения не загружен: файла нет, он не читается или содержит ошибку. Сообщение — для
    /// пользователя; <see cref="FieldName"/> — поле файла настроек, которое надо исправить.
    /// </summary>
    public sealed class ProfileLoadException : Exception
    {
        public ProfileLoadException(string message, string? fieldName = null, Exception? innerException = null)
            : base(message, innerException)
        {
            FieldName = fieldName;
        }

        /// <summary>Поле файла настроек с ошибкой; <c>null</c> — ошибка относится к файлу целиком.</summary>
        public string? FieldName { get; }
    }
}
