using System;

namespace IwoBridge.Core.Security
{
    /// <summary>
    /// Учётные данные для входа на сервер-приёмник. Читаются из диспетчера учётных данных Windows и нигде
    /// не сохраняются продуктом (политика конфиденциальности).
    ///
    /// Имя в форме <c>ДОМЕН\пользователь</c> разбирается на домен и пользователя: NTLM ожидает домен
    /// отдельным полем. UPN (<c>user@domain</c>) и простое имя передаются как есть.
    /// </summary>
    public sealed class CredentialSecret
    {
        public CredentialSecret(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                throw new ArgumentException("Имя пользователя обязательно.", nameof(userName));
            }

            Password = password ?? throw new ArgumentNullException(nameof(password));

            // «ДОМЕН\пользователь»: ровно одна обратная косая с непустыми частями (пробелы вокруг частей убираются).
            // «\пользователь», «ДОМЕН\» и «А\Б\В» — опечатки: NTLM с ними не пройдёт, а счётчик неудачных входов
            // доменной политики растёт. Лучше сказать сразу.
            string trimmed = userName.Trim();
            int separator = trimmed.IndexOf('\\');
            if (separator < 0)
            {
                Domain = null;
                User = trimmed;
                UserName = trimmed;
                return;
            }

            string domain = trimmed.Substring(0, separator).Trim();
            string user = trimmed.Substring(separator + 1).Trim();
            if (domain.Length == 0 || user.Length == 0 || user.IndexOf('\\') >= 0)
            {
                throw new ArgumentException(
                    "Учётная запись указывается как ДОМЕН\\пользователь или пользователь@домен.", nameof(userName));
            }

            Domain = domain;
            User = user;
            UserName = domain + "\\" + user;
        }

        /// <summary>Имя пользователя как введено, например <c>ДОМЕН\пользователь</c>.</summary>
        public string UserName { get; }

        /// <summary>Домен из формы <c>ДОМЕН\пользователь</c>; для UPN и простого имени — <c>null</c>.</summary>
        public string? Domain { get; }

        /// <summary>Имя пользователя без домена (или UPN целиком).</summary>
        public string User { get; }

        /// <summary>Пароль.</summary>
        public string Password { get; }
    }
}
