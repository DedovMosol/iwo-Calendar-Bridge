using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace IwoBridge.Ews.Transport
{
    /// <summary>
    /// Единый процессный обработчик проверки сертификатов, который передаёт решение валидатору своего подключения.
    ///
    /// Зачем: в .NET Framework проверка сертификата задаётся одна на процесс
    /// (<see cref="ServicePointManager.ServerCertificateValidationCallback"/>). Если каждое подключение ставит туда свой
    /// валидатор, то проверка подключения в окне настроек подменяет проверку действующего сеанса (и при отмене окна
    /// не возвращает её), а TLS-соединения, открытые раньше, переиспользуются без новой проверки.
    ///
    /// Как: у каждого подключения (<see cref="EwsTarget"/>) своя группа соединений — уникальное
    /// <c>ExchangeService.ConnectionGroupName</c>. EWS Managed API копирует его в каждый <see cref="HttpWebRequest"/>
    /// (проверено по коду библиотеки 2.2), а .NET держит для группы отдельный пул соединений. Обработчик ставится один
    /// раз и по группе запроса находит валидатор; чужие соединения процесса проверяются стандартно (только без ошибок).
    /// При закрытии подключения группа снимается с учёта и её соединения закрываются. Класс потокобезопасен.
    /// </summary>
    internal static class TlsTrustRegistry
    {
        private static readonly ConcurrentDictionary<string, EwsCertificateValidator> Validators =
            new ConcurrentDictionary<string, EwsCertificateValidator>(StringComparer.Ordinal);

        private static int _installed;

        /// <summary>Регистрирует валидатор группы соединений и (один раз за процесс) ставит общий обработчик.</summary>
        public static void Register(string connectionGroupName, EwsCertificateValidator validator)
        {
            if (string.IsNullOrEmpty(connectionGroupName))
            {
                throw new ArgumentException("Нужно имя группы соединений.", nameof(connectionGroupName));
            }

            if (validator is null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            if (!Validators.TryAdd(connectionGroupName, validator))
            {
                throw new InvalidOperationException("Группа соединений уже зарегистрирована.");
            }

            if (Interlocked.Exchange(ref _installed, 1) == 0)
            {
                ServicePointManager.ServerCertificateValidationCallback = Validate;
            }
        }

        /// <summary>Снимает валидатор группы с учёта.</summary>
        public static void Unregister(string connectionGroupName)
        {
            Validators.TryRemove(connectionGroupName, out _);
        }

        /// <summary>Число зарегистрированных групп (для тестов: проверка, что подключения не «текут»).</summary>
        internal static int Count => Validators.Count;

        /// <summary>Решение по соединению: валидатор группы запроса или стандартная проверка.</summary>
        internal static bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (sender is HttpWebRequest request
                && request.ConnectionGroupName != null
                && Validators.TryGetValue(request.ConnectionGroupName, out EwsCertificateValidator? validator))
            {
                return validator.Validate(sender, certificate, chain, errors);
            }

            return errors == SslPolicyErrors.None;
        }
    }
}
