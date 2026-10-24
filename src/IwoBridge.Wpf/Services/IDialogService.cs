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

        /// <summary>Вопрос «да/нет»; true — да.</summary>
        bool Ask(string title, string question);

        /// <summary>Открыть проводник с выделенным файлом.</summary>
        void ShowFileInFolder(string path);
    }

    /// <summary>Окно «Подключение» (модальное); true — настройки сохранены.</summary>
    public interface IConnectionDialog
    {
        bool Show(ConnectionViewModel connection);
    }
}
