using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IwoBridge.Core.Security;

namespace IwoBridge.Wpf.Services
{
    /// <summary>Добавленная почта (ящики для очистки сверх приёмника и источника): по подключению на ящик.</summary>
    public interface IMailboxStores
    {
        /// <summary>Запомненные ящики в порядке добавления.</summary>
        IReadOnlyList<ISettingsStore> Saved();

        /// <summary>Место для нового ящика. На диске ничего не появляется, пока подключение не сохранено.</summary>
        ISettingsStore CreateNew();
    }

    /// <summary>
    /// Ящики в папке <c>config\mailboxes</c>: профиль — файл <c>&lt;номер&gt;.json</c>, пароль — запись
    /// <c>IwoCalendarBridge/mailbox/&lt;номер&gt;</c> в диспетчере учётных данных Windows. Номер — случайный GUID без
    /// дефисов. Файлы с другими именами не трогаются.
    /// </summary>
    public sealed class MailboxStores : IMailboxStores
    {
        private readonly string _directory;
        private readonly ICredentialStore _credentials;

        public MailboxStores(string directory, ICredentialStore credentials)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        }

        public IReadOnlyList<ISettingsStore> Saved()
        {
            if (!Directory.Exists(_directory))
            {
                return Array.Empty<ISettingsStore>();
            }

            return new DirectoryInfo(_directory).GetFiles("*.json")
                .Where(f => IsId(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderBy(f => f.CreationTimeUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => Store(Path.GetFileNameWithoutExtension(f.Name)))
                .ToList();
        }

        public ISettingsStore CreateNew() => Store(Guid.NewGuid().ToString("N"));

        private ISettingsStore Store(string id) =>
            new AppSettingsStore(Path.Combine(_directory, id + ".json"), _credentials, CredentialTargets.MailboxPrefix + id);

        private static bool IsId(string name) => name.Length == 32 && Guid.TryParseExact(name, "N", out _);
    }
}
