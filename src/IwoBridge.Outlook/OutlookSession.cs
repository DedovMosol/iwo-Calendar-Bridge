using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.Core.Sync;

namespace IwoBridge.Outlook
{
    /// <summary>Календарь в Outlook этого компьютера: свой, другого ящика профиля или общий (чужой).</summary>
    public sealed class OutlookCalendarRef
    {
        internal OutlookCalendarRef(string label, string entryId, string storeId, bool isCurrent = false, string? owner = null)
        {
            Label = label;
            EntryId = entryId;
            StoreId = storeId;
            IsCurrent = isCurrent;
            Owner = owner;
        }

        /// <summary>Как показать человеку: «Календарь — ящик» или «Имя — Общие календари».</summary>
        public string Label { get; }

        /// <summary>Этот календарь сейчас открыт в окне Outlook.</summary>
        public bool IsCurrent { get; }

        /// <summary>Идентификатор папки (EntryID) — по нему календарь узнаётся при следующем чтении списка.</summary>
        public string EntryId { get; }

        internal string StoreId { get; }

        /// <summary>Владелец (как его ввёл человек) — для общего календаря, найденного по имени; иначе <c>null</c>.</summary>
        internal string? Owner { get; }
    }

    /// <summary>
    /// Работа с Outlook этого компьютера — только чтение. Outlook должен быть запущен: программа подключается
    /// к уже открытому Outlook и не запускает его сама — иначе он открылся бы скрытно, мог бы спросить профиль или пароль,
    /// и пользователь этого не увидел бы. Все вызовы — в отдельном потоке STA (<see cref="StaWorker"/>).
    ///
    /// Долгая работа (программа в области уведомлений неделями):
    ///  - Outlook закрыли и открыли снова — прежний объект Outlook «мёртв» (RPC_E_DISCONNECTED); он отбрасывается, и
    ///    программа подключается к новому Outlook при следующем вызове;
    ///  - вызов завис (Outlook ждёт ответа в своём окне) — через <see cref="CallTimeout"/> ошибка, поток STA заменяется
    ///    новым (<see cref="StaWorker.IsBroken"/>);
    ///  - каждый объект COM освобождается сразу после использования (<see cref="Com.Release"/>).
    /// </summary>
    public sealed class OutlookSession : IDisposable
    {
        /// <summary>Сколько ждать один вызов Outlook (чтение календаря целиком — один вызов).</summary>
        public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

        private const int OlFolderCalendar = 9;
        private const int OlAppointmentItem = 1;
        private const int OlModuleCalendar = 1;

        // Outlook ушёл: сервер RPC недоступен, объект отключён от клиентов, вызов отклонён отключившимся сервером.
        private static readonly int[] Disconnected = { unchecked((int)0x800706BA), unchecked((int)0x80010108), unchecked((int)0x800401FD), unchecked((int)0x80010114) };

        private const int ObjectUnavailable = unchecked((int)0x800401E3); // MK_E_UNAVAILABLE: нет в таблице запущенных объектов

        private readonly object _sync = new object();
        private StaWorker _worker = new StaWorker();
        private dynamic? _app;

        /// <summary>Установлен ли Outlook на компьютере (без запуска).</summary>
        public static bool IsInstalled => Type.GetTypeFromProgID("Outlook.Application") != null;

        /// <summary>
        /// Календари открытого Outlook — те же, что человек видит в панели «Календарь»: свои, других ящиков профиля и
        /// «Общие календари» (чужие, открытые в Outlook). Плюс основной календарь каждого ящика профиля и его вложенные
        /// календари первого уровня. Календарь, открытый сейчас в окне Outlook, помечен (<see cref="OutlookCalendarRef.IsCurrent"/>).
        /// Глубже папки не обходятся: в больших общих ящиках в режиме «онлайн» полный обход шёл бы минутами.
        /// </summary>
        /// <exception cref="OutlookNotRunningException">Outlook не запущен или не установлен.</exception>
        public Task<IReadOnlyList<OutlookCalendarRef>> ListCalendarsAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync<IReadOnlyList<OutlookCalendarRef>>(
                ns =>
                {
                    var list = new List<OutlookCalendarRef>();
                    AddNavigationPane((object)ns, list);
                    dynamic stores = ns.Stores;
                    try
                    {
                        int count = (int)stores.Count;
                        for (int i = 1; i <= count; i++)
                        {
                            dynamic? store = null;
                            dynamic? calendar = null;
                            dynamic? folders = null;
                            try
                            {
                                store = stores.Item(i);
                                string storeName = (string)store.DisplayName;
                                calendar = store.GetDefaultFolder(OlFolderCalendar);
                                Add((object)ns, list, FolderName(calendar) + " — " + storeName, calendar, false);
                                folders = calendar.Folders;
                                int subCount = (int)folders.Count;
                                for (int j = 1; j <= subCount; j++)
                                {
                                    dynamic sub = folders.Item(j);
                                    try
                                    {
                                        if ((int)sub.DefaultItemType == OlAppointmentItem)
                                        {
                                            Add((object)ns, list, FolderName(sub) + " — " + storeName, sub, false);
                                        }
                                    }
                                    finally
                                    {
                                        Com.Release(sub);
                                    }
                                }
                            }
#pragma warning disable CA1031 // Ящик без календаря или недоступный (архив, отключённый общий ящик) просто не показывается.
                            catch (Exception)
#pragma warning restore CA1031
                            {
                            }
                            finally
                            {
                                Com.Release(folders);
                                Com.Release(calendar);
                                Com.Release(store);
                            }
                        }
                    }
                    finally
                    {
                        Com.Release(stores);
                    }

                    return list;
                },
                cancellationToken);
        }

        /// <summary>
        /// Общий календарь по имени владельца (ФИО или адрес, как в адресной книге Outlook) — если его нет в панели
        /// «Календарь». Нужны права на чтение его календаря.
        /// </summary>
        /// <exception cref="OutlookCalendarNotFoundException">Имя не найдено или нет доступа к календарю.</exception>
        public Task<OutlookCalendarRef> FindSharedAsync(string owner, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new ArgumentException("Нужно имя владельца календаря.", nameof(owner));
            }

            string name = owner.Trim();
            return RunAsync<OutlookCalendarRef>(
                ns =>
                {
                    dynamic folder = SharedCalendar((object)ns, name, out string resolved);
                    try
                    {
                        return new OutlookCalendarRef(resolved + " — общий календарь", (string)folder.EntryID, (string)folder.StoreID, owner: name);
                    }
                    finally
                    {
                        Com.Release(folder);
                    }
                },
                cancellationToken);
        }

        /// <summary>Календарь Outlook как источник копирования.</summary>
        public ISourceCalendar Open(OutlookCalendarRef calendar)
        {
            if (calendar is null)
            {
                throw new ArgumentNullException(nameof(calendar));
            }

            return new OutlookSource(this, calendar);
        }

        public void Dispose()
        {
            StaWorker worker;
            lock (_sync)
            {
                worker = _worker;
            }

            // Освобождение — в потоке, где объект получен; зависший поток освобождать некому (он занят до конца процесса).
            if (!worker.IsBroken)
            {
                worker.RunAsync(
                    () =>
                    {
                        Com.Release(_app);
                        _app = null;
                        return true;
                    },
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);
            }

            worker.Dispose();
        }

        /// <summary>
        /// Вызов Outlook в потоке STA. Outlook перезапускали — один повтор с новым подключением; поток завис — новый поток.
        /// </summary>
        internal Task<T> RunAsync<T>(Func<dynamic, T> work, CancellationToken cancellationToken)
        {
            StaWorker worker;
            lock (_sync)
            {
                if (_worker.IsBroken)
                {
                    // Объект Outlook принадлежит зависшему потоку: здесь он не освобождается, а забывается.
                    _worker.Dispose();
                    _worker = new StaWorker();
                    _app = null;
                }

                worker = _worker;
            }

            return worker.RunAsync<T>(
                () =>
                {
                    try
                    {
                        return CallOnce(work);
                    }
                    catch (COMException ex) when (Array.IndexOf(Disconnected, ex.ErrorCode) >= 0)
                    {
                        Forget();
                        return CallOnce(work);
                    }
                    catch (InvalidComObjectException)
                    {
                        Forget();
                        return CallOnce(work);
                    }
                },
                CallTimeout,
                cancellationToken);
        }

        /// <summary>
        /// Папка календаря: по идентификатору, а общий календарь, найденный по имени, — заново через адресную книгу, если
        /// идентификатор перестал открываться (Outlook перезапущен, кэш общих папок сброшен).
        /// </summary>
        internal static dynamic OpenFolder(dynamic ns, OutlookCalendarRef calendar)
        {
            try
            {
                return ns.GetFolderFromID(calendar.EntryId, calendar.StoreId);
            }
            catch (COMException) when (calendar.Owner != null)
            {
                return SharedCalendar((object)ns, calendar.Owner!, out _);
            }
        }

        private T CallOnce<T>(Func<dynamic, T> work)
        {
            dynamic ns = Namespace();
            try
            {
                return work(ns);
            }
            finally
            {
                Com.Release(ns);
            }
        }

        private void Forget()
        {
            Com.Release(_app);
            _app = null;
        }

        private static dynamic SharedCalendar(dynamic ns, string owner, out string resolved)
        {
            dynamic recipient = ns.CreateRecipient(owner);
            try
            {
                if (!(bool)recipient.Resolve())
                {
                    throw new OutlookCalendarNotFoundException(
                        "«" + owner + "» не найден ни среди календарей Outlook, ни в адресной книге. Откройте этот календарь в Outlook "
                        + "(он появится в «Общих календарях») и нажмите «Обновить», либо введите имя точнее.");
                }

                resolved = (string)recipient.Name;
                try
                {
                    return ns.GetSharedDefaultFolder(recipient, OlFolderCalendar);
                }
                catch (COMException ex)
                {
                    throw new OutlookCalendarNotFoundException(
                        "Нет доступа к календарю «" + resolved + "»: владелец не открыл его вам, или сервер его ящика недоступен. " + ex.Message, ex);
                }
            }
            finally
            {
                Com.Release(recipient);
            }
        }

        /// <summary>
        /// Календари панели «Календарь» Outlook по группам («Мои календари», «Общие календари»…). Открытый сейчас — тот,
        /// что отмечен в панели (IsSelected): идентификатор CurrentFolder окна у общих календарей длинный и строкой не
        /// совпадает с идентификатором той же папки в панели (проверено вживую).
        /// </summary>
        private void AddNavigationPane(dynamic ns, List<OutlookCalendarRef> list)
        {
            dynamic? explorer = null, pane = null, modules = null, module = null, groups = null;
            try
            {
                explorer = _app!.ActiveExplorer();
                if (explorer is null)
                {
                    return;
                }

                pane = explorer.NavigationPane;
                modules = pane.Modules;
                module = modules.GetNavigationModule(OlModuleCalendar);
                groups = module.NavigationGroups;
                int groupCount = (int)groups.Count;
                for (int g = 1; g <= groupCount; g++)
                {
                    dynamic group = groups.Item(g);
                    dynamic folders = group.NavigationFolders;
                    try
                    {
                        string groupName = (string)group.Name;
                        int count = (int)folders.Count;
                        for (int f = 1; f <= count; f++)
                        {
                            dynamic? navigation = null, folder = null;
                            try
                            {
                                navigation = folders.Item(f);
                                folder = navigation.Folder;
                                Add((object)ns, list, (string)navigation.DisplayName + " — " + groupName, folder, (bool)navigation.IsSelected);
                            }
#pragma warning disable CA1031 // Недоступный общий календарь (нет прав, сервер владельца недоступен) просто не показывается.
                            catch (Exception)
#pragma warning restore CA1031
                            {
                            }
                            finally
                            {
                                Com.Release(folder);
                                Com.Release(navigation);
                            }
                        }
                    }
                    finally
                    {
                        Com.Release(folders);
                        Com.Release(group);
                    }
                }
            }
#pragma warning disable CA1031 // Нет окна Outlook или панели (старая версия) — остаются календари ящиков профиля.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                Com.Release(groups);
                Com.Release(module);
                Com.Release(modules);
                Com.Release(pane);
                Com.Release(explorer);
            }
        }

        private dynamic Namespace()
        {
            if (_app is null)
            {
                _app = Connect();
            }

            return _app.GetNamespace("MAPI");
        }

        /// <summary>
        /// Подключиться к запущенному Outlook. Обычно он есть в таблице запущенных объектов; если нет (известная
        /// особенность Office, разный уровень прав) — создать объект Outlook: это подключение к уже запущенному
        /// единственному экземпляру. Создание — только если процесс Outlook есть: иначе Outlook запустился бы скрытно.
        /// </summary>
        private static object Connect()
        {
            try
            {
                return Marshal.GetActiveObject("Outlook.Application");
            }
            catch (COMException ex) when (ex.ErrorCode == ObjectUnavailable && OutlookProcessRunning())
            {
                Type? type = Type.GetTypeFromProgID("Outlook.Application");
                if (type != null)
                {
                    try
                    {
                        return Activator.CreateInstance(type)!;
                    }
                    catch (COMException inner)
                    {
                        throw new OutlookNotRunningException(inner);
                    }
                }

                throw new OutlookNotRunningException(ex);
            }
            catch (COMException ex)
            {
                throw new OutlookNotRunningException(ex);
            }
        }

        private static bool OutlookProcessRunning()
        {
            Process[] found = Process.GetProcessesByName("OUTLOOK");
            foreach (Process process in found)
            {
                process.Dispose();
            }

            return found.Length > 0;
        }

        private static string FolderName(dynamic folder)
        {
            string path = (string)folder.FolderPath;
            return path.Substring(path.LastIndexOf('\\') + 1);
        }

        /// <summary>
        /// Добавить календарь, если его ещё нет. Одна папка бывает в нескольких группах панели и среди ящиков профиля, а
        /// её идентификатор — в разной записи: сравнение — средствами MAPI (CompareEntryIDs), не строкой.
        /// </summary>
        private static void Add(dynamic ns, List<OutlookCalendarRef> list, string label, dynamic folder, bool isCurrent)
        {
            string entryId = (string)folder.EntryID;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].EntryId, entryId, StringComparison.OrdinalIgnoreCase) || (bool)ns.CompareEntryIDs(list[i].EntryId, entryId))
                {
                    if (isCurrent && !list[i].IsCurrent)
                    {
                        list[i] = new OutlookCalendarRef(list[i].Label, list[i].EntryId, list[i].StoreId, true, list[i].Owner);
                    }

                    return;
                }
            }

            list.Add(new OutlookCalendarRef(label, entryId, (string)folder.StoreID, isCurrent));
        }
    }

    /// <summary>Outlook не открыт (или не установлен) — источник «Outlook» недоступен.</summary>
    public sealed class OutlookNotRunningException : Exception
    {
        public OutlookNotRunningException(Exception inner)
            : base("Outlook не запущен. Откройте Outlook на этом компьютере и нажмите «Обновить».", inner)
        {
        }
    }

    /// <summary>Общий календарь по имени не найден или к нему нет доступа.</summary>
    public sealed class OutlookCalendarNotFoundException : Exception
    {
        public OutlookCalendarNotFoundException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
}
