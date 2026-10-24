using System;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Действующее подключение к календарю-приёмнику — всё, что нужно экрану очистки (DIP): сценарий, пояс для
    /// показа времени, что именно очищается и чем подтверждать удаление. Реализации: настоящий сервер
    /// (<see cref="EwsSession"/>) и демонстрационный календарь в памяти (<see cref="DemoSession"/>).
    /// </summary>
    /// <remarks>
    /// Владелец сеанса — главное окно: при смене подключения прежний сеанс закрывается (<see cref="IDisposable.Dispose"/>),
    /// освобождая свою группу соединений и проверку сертификата.
    /// </remarks>
    public interface ITargetSession : IDisposable
    {
        /// <summary>Что очищается — для заголовка окна, например «user@example.ru на mail.example.ru».</summary>
        string TargetLabel { get; }

        /// <summary>
        /// Что пользователь вводит для подтверждения удаления: адрес ящика либо имя учётной записи, если очищается
        /// её собственный ящик. Подтверждается ровно тот ящик, в который пойдут запросы (T33).
        /// </summary>
        string ConfirmationPhrase { get; }

        /// <summary>Пояс приёмника: в нём задаётся период и показывается время.</summary>
        TargetTimeZone TimeZone { get; }

        CleanupRunner Runner { get; }

        /// <summary>Демонстрационные данные — окно показывает это постоянно, чтобы не спутать с настоящим ящиком.</summary>
        bool IsDemo { get; }

        /// <summary>Понятное описание ошибки подключения или сервера.</summary>
        UserMessage Describe(Exception exception);
    }
}
