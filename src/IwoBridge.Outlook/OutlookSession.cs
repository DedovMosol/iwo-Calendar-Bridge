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

        // Outlook ушёл: сервер RPC недоступен (0x800706BA), объект отключён от клиентов (0x80010108, 0x800401FD), вызов
        // отклонён отключившимся сервером (0x80010114), сервер RPC завершился во время вызова (0x80010007, 0x80010012),
        // вызов RPC не выполнен (0x800706BE).
        private static readonly int[] Disconnected =
        {
            unchecked((int)0x800706BA), unchecked((int)0x80010108), unchecked((int)0x800401FD), unchecked((int)0x80010114),
            unchecked((int)0x80010007), unchecked((int)0x80010012), unchecked((int)0x800706BE),
        };

        private const int ObjectUnavailable = unchecked((int)0x800401E3); // MK_E_UNAVAILABLE: нет в таблице запущенных объектов
        private const int CallRejected = unchecked((int)0x80010001); // RPC_E_CALL_REJECTED: Outlook занят дольше минуты

        private readonly object _sync = new object();
        private StaWorker _worker = new StaWorker();

        // Последний зависший поток: пока он жив, второй зависший поток не заводится — вызовы сразу получают ошибку.
        private StaWorker? _hung;

        /// <summary>
        /// Сбой не одного события, а всего Outlook: он закрыт, упал или занят. Чтение такого сбоя не глотает по событию —
        /// оно прерывается целиком (и после переподключения повторяется), а не считает все оставшиеся события непрочитанными.
        /// </summary>
        internal static bool IsFatal(Exception exception) =>
            exception is InvalidComObjectException
            || exception is OutlookBusyException
            || (exception is COMException com && (com.ErrorCode == CallRejected || Array.IndexOf(Disconnected, com.ErrorCode) >= 0));

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
                            catch (Exception ex) when (!IsFatal(ex))
#pragma warning restore CA1031
                            {
                            }
                            finally
                            {
                                Com.Release(folders);
                                Com.Release(calendar);
                                Com.Release(store);
                            }

                            StaWorker.Touch();
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
                        Forget(worker);
                        return true;
                    },
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);
            }

            worker.Dispose();
        }

        /// <summary>
        /// Вызов Outlook в потоке STA. Outlook перезапускали — один повтор с новым подключением; поток завис — новый поток
        /// (но не больше одного зависшего сразу: если и прежний ещё не вернулся, Outlook явно не отвечает).
        /// </summary>
        internal Task<T> RunAsync<T>(Func<dynamic, T> work, CancellationToken cancellationToken)
        {
            StaWorker worker;
            lock (_sync)
            {
                if (_worker.IsBroken)
                {
                    if (_hung != null && _hung.IsAlive)
                    {
                        return Task.FromException<T>(new TimeoutException(
                            "Outlook не отвечает: прежние обращения к нему ещё не вернулись. Проверьте, нет ли в Outlook открытого окна "
                            + "с вопросом, или перезапустите Outlook."));
                    }

                    // Объект Outlook принадлежит зависшему потоку и остаётся у него: новый поток подключается заново.
                    _hung = _worker;
                    _worker.Dispose();
                    _worker = new StaWorker();
                }

                worker = _worker;
            }

            return worker.RunAsync<T>(
                () =>
                {
                    try
                    {
                        return CallOnce(worker, work);
                    }
                    catch (COMException ex) when (Array.IndexOf(Disconnected, ex.ErrorCode) >= 0)
                    {
                        Forget(worker);
                        return CallOnce(worker, work);
                    }
                    catch (InvalidComObjectException)
                    {
                        Forget(worker);
                        return CallOnce(worker, work);
                    }
                    catch (COMException ex) when (ex.ErrorCode == CallRejected)
                    {
                        throw new OutlookBusyException(ex);
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
            catch (COMException ex) when (calendar.Owner != null && !IsFatal(ex))
            {
                return SharedCalendar((object)ns, calendar.Owner!, out _);
            }
        }

        private static T CallOnce<T>(StaWorker worker, Func<dynamic, T> work)
        {
            dynamic ns = Namespace(worker);
            try
            {
                return work(ns);
            }
            finally
            {
                Com.Release(ns);
            }
        }

        private static void Forget(StaWorker worker)
        {
            Com.Release(worker.App);
            worker.App = null;
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
        private static void AddNavigationPane(dynamic ns, List<OutlookCalendarRef> list)
        {
            dynamic? app = null, explorer = null, pane = null, modules = null, module = null, groups = null;
            try
            {
                app = ns.Application;
                explorer = app.ActiveExplorer();
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
                    dynamic? group = null, folders = null;
                    try
                    {
                        group = groups.Item(g);
                        folders = group.NavigationFolders;
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
                            catch (Exception ex) when (!IsFatal(ex))
#pragma warning restore CA1031
                            {
                            }
                            finally
                            {
                                Com.Release(folder);
                                Com.Release(navigation);
                            }

                            StaWorker.Touch();
                        }
                    }
#pragma warning disable CA1031 // Группа панели не открылась — остальные группы читаются.
                    catch (Exception ex) when (!IsFatal(ex))
#pragma warning restore CA1031
                    {
                    }
                    finally
                    {
                        Com.Release(folders);
                        Com.Release(group);
                    }
                }
            }
#pragma warning disable CA1031 // Нет окна Outlook или панели (старая версия) — остаются календари ящиков профиля.
            catch (Exception ex) when (!IsFatal(ex))
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
                Com.Release(app);
            }
        }

        private static dynamic Namespace(StaWorker worker)
        {
            if (worker.App is null)
            {
                worker.App = Connect();
            }

            return ((dynamic)worker.App).GetNamespace("MAPI");
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

        /// <summary>
        /// Outlook запущен в сеансе этого пользователя. Процессы других сеансов (сервер терминалов, быстрое переключение
        /// пользователей) не в счёт: создание объекта тогда запустило бы скрытый Outlook здесь.
        /// </summary>
        private static bool OutlookProcessRunning()
        {
            int session;
            using (Process self = Process.GetCurrentProcess())
            {
                session = self.SessionId;
            }

            bool running = false;
            foreach (Process process in Process.GetProcessesByName("OUTLOOK"))
            {
                using (process)
                {
                    running |= process.SessionId == session;
                }
            }

            return running;
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

    /// <summary>Outlook дольше минуты отклоняет обращения: в нём открыто окно или идёт долгая синхронизация.</summary>
    public sealed class OutlookBusyException : Exception
    {
        public OutlookBusyException(Exception inner)
            : base("Outlook занят: в нём открыто окно или идёт синхронизация. Закройте окно в Outlook и повторите.", inner)
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
