using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Подключение к приёмнику по введённым настройкам: проверка (без сохранения) и открытие сеанса для очистки.
    /// Реализация — <see cref="EwsConnector"/>; в тестах — подставной.
    /// </summary>
    public interface ITargetConnector
    {
        /// <summary>Пробное подключение. Не выбрасывает при ошибках сервера — возвращает их в результате.</summary>
        Task<ConnectionCheckResult> CheckAsync(ConnectionProfile profile, CredentialSecret credentials, CancellationToken cancellationToken);

        /// <summary>Сеанс для экрана очистки. Сеть не трогает — соединение устанавливается при первом запросе.</summary>
        ITargetSession Open(ConnectionProfile profile, CredentialSecret credentials);
    }
}
