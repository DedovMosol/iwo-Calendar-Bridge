using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Окна и системные действия, которые модель представления вызывает, но не создаёт сама (SoC): так модели
    /// тестируются без окон, а тест подставляет ответы пользователя.
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Окно подтверждения удаления; true — пользователь подтвердил.</summary>
        bool ConfirmDeletion(DeletionConfirmationViewModel confirmation);

        /// <summary>
        /// Вопрос с двумя кнопками, подписанными действием: <paramref name="confirm"/> («Скопировать») и
        /// <paramref name="cancel"/> («Отмена»). true — человек выбрал действие; Enter, Esc и закрытие окна — отказ.
        /// </summary>
        bool Ask(string title, string question, string confirm, string cancel = "Отмена");

        /// <summary>Открыть проводник с выделенным файлом.</summary>
        void ShowFileInFolder(string path);

        /// <summary>Окно справки (одно на программу; повторный вызов — вывести его вперёд).</summary>
        void ShowHelp();

        /// <summary>
        /// Окно «История» (одно на программу; повторный вызов — вывести его вперёд). Модель создаётся, только если окна ещё
        /// нет: открытое окно уже слушает журнал.
        /// </summary>
        void ShowHistory(System.Func<HistoryViewModel> create);

        /// <summary>
        /// Открыто модальное окно (вопрос, «Подключение», подтверждение удаления): человек решает — автоматические
        /// операции ждут, иначе они изменили бы календарь под открытым вопросом.
        /// </summary>
        bool IsShowingDialog { get; }
    }

    /// <summary>Окно «Подключение» (модальное); true — настройки сохранены.</summary>
    public interface IConnectionDialog
    {
        bool Show(ConnectionViewModel connection);
    }
}
