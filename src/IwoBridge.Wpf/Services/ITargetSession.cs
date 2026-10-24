using System;
using IwoBridge.App.Cleanup;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Открытое подключение к календарю ящика (по EWS или демонстрационное в памяти). Один и тот же ящик может быть:
    ///  - приёмником копирования (<see cref="CopyTarget"/>);
    ///  - источником копирования (<see cref="Source"/>, только чтение);
    ///  - очищаемым календарём (<see cref="Runner"/>).
    /// Сеть при создании не трогается — соединение устанавливается при первом запросе. Освобождение закрывает
    /// соединения и проверку сертификата подключения.
    /// </summary>
    public interface ITargetSession : IDisposable
    {
        /// <summary>Что это за календарь — показывается человеку («ящик на сервер»).</summary>
        string TargetLabel { get; }

        /// <summary>Что ввести для подтверждения удаления (адрес ящика или имя учётной записи).</summary>
        string ConfirmationPhrase { get; }

        /// <summary>Пояс календаря — для показа времени и границ периода.</summary>
        TargetTimeZone TimeZone { get; }

        /// <summary>Сценарий очистки этого календаря.</summary>
        CleanupRunner Runner { get; }

        /// <summary>Этот календарь как приёмник копирования.</summary>
        ICopyTarget CopyTarget { get; }

        /// <summary>Этот календарь как источник копирования (только чтение).</summary>
        ISourceCalendar Source { get; }

        bool IsDemo { get; }

        /// <summary>Понятное сообщение об ошибке операции с этим календарём.</summary>
        UserMessage Describe(Exception exception);

        /// <summary>Ошибка всего подключения (вход, связь, права) — пакетная запись на ней останавливается.</summary>
        bool StopsBatch(Exception exception);
    }
}
