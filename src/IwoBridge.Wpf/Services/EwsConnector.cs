using System;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Security;
using IwoBridge.Ews;
using IwoBridge.Wpf.Messages;

namespace IwoBridge.Wpf.Services
{
    /// <summary>
    /// Подключение по EWS из окна настроек: пробный запрос к календарю с разбором ответа и сертификата, открытие
    /// сеанса для очистки. Ошибки сервера не выбрасываются, а возвращаются с понятным текстом. Пробное подключение
    /// живёт только на время проверки: своя группа соединений и своя проверка сертификата, после — закрывается и
    /// действующему сеансу не мешает.
    /// </summary>
    public sealed class EwsConnector : ITargetConnector
    {
        private readonly string _manifestDirectory;

        public EwsConnector(string manifestDirectory)
        {
            _manifestDirectory = manifestDirectory ?? throw new ArgumentNullException(nameof(manifestDirectory));
        }

        public async Task<ConnectionCheckResult> CheckAsync(
            ConnectionProfile profile, CredentialSecret credentials, CancellationToken cancellationToken)
        {
            EwsTarget? target = null;
            try
            {
                target = new EwsTarget(profile, credentials);
                int count = await target.CheckAsync(cancellationToken).ConfigureAwait(true);
                return ConnectionCheckResult.Success(count, target.CertificateDecision, target.PresentedCertificate, target.PinnedThumbprint);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Любая ошибка подключения — результат проверки с понятным текстом.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ConnectionCheckResult.Failure(
                    ErrorMessages.For(EwsTarget.Classify(ex), EwsTarget.Explain(ex)),
                    target?.CertificateDecision,
                    target?.PresentedCertificate);
            }
            finally
            {
                target?.Dispose();
            }
        }

        public ITargetSession Open(ConnectionProfile profile, CredentialSecret credentials)
        {
            return new EwsSession(profile, credentials, _manifestDirectory);
        }
    }
}
