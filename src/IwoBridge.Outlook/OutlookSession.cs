using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        internal string EntryId { get; }

        internal string StoreId { get; }

        /// <summary>Владелец (как его ввёл человек) — для общего календаря, найденного по имени; иначе <c>null</c>.</summary>
        internal string? Owner { get; }
    }

    /// <summary>
    /// Работа с Outlook этого компьютера — только чтение (PLAN.md §9). Outlook должен быть запущен: программа подключается
    /// к уже открытому Outlook и не запускает его сама — иначе он открылся бы скрытно, мог бы спросить профиль или пароль,
    /// и пользователь этого не увидел бы. Все вызовы — в отдельном потоке STA (<see cref="StaWorker"/>).
    /// </summary>
    public sealed class OutlookSession : IDisposable
    {
        private const int OlFolderCalendar = 9;
        private const int OlAppointmentItem = 1;
        private const int OlModuleCalendar = 1;

        private readonly StaWorker _worker = new StaWorker();
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
        public Task<IReadOnlyList<OutlookCalendarRef>> ListCalendarsAsync()
        {
            return _worker.RunAsync<IReadOnlyList<OutlookCalendarRef>>(() =>
            {
                dynamic ns = Namespace();
                var list = new List<OutlookCalendarRef>();
                AddNavigationPane((object)ns, list);
                foreach (dynamic store in ns.Stores)
                {
                    try
                    {
                        string storeName = (string)store.DisplayName;
                        dynamic calendar = store.GetDefaultFolder(OlFolderCalendar);
                        Add((object)ns, list, FolderName(calendar) + " — " + storeName, calendar, false);
                        foreach (dynamic sub in calendar.Folders)
                        {
                            if ((int)sub.DefaultItemType == OlAppointmentItem)
                            {
                                Add((object)ns, list, FolderName(sub) + " — " + storeName, sub, false);
                            }
                        }
                    }
#pragma warning disable CA1031 // Ящик без календаря или недоступный (архив, отключённый общий ящик) просто не показывается.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                }

                return list;
            });
        }

        /// <summary>
        /// Общий календарь по имени владельца (ФИО или адрес, как в адресной книге Outlook) — если его нет в панели
        /// «Календарь». Нужны права на чтение его календаря.
        /// </summary>
        /// <exception cref="OutlookCalendarNotFoundException">Имя не найдено или нет доступа к календарю.</exception>
        public Task<OutlookCalendarRef> FindSharedAsync(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new ArgumentException("Нужно имя владельца календаря.", nameof(owner));
            }

            string name = owner.Trim();
            return _worker.RunAsync<OutlookCalendarRef>(() =>
            {
                object ns = Namespace();
                dynamic folder = SharedCalendar(ns, name, out string resolved);
                return new OutlookCalendarRef(resolved + " — общий календарь", (string)folder.EntryID, (string)folder.StoreID, owner: name);
            });
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
            _worker.RunAsync(() =>
            {
                if (_app != null)
                {
                    Marshal.FinalReleaseComObject(_app);
                    _app = null;
                }

                return true;
            });
            _worker.Dispose();
        }

        internal Task<T> RunAsync<T>(Func<dynamic, T> work) => _worker.RunAsync<T>(() =>
        {
            object ns = Namespace();
            return work(ns);
        });

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

        private static dynamic SharedCalendar(dynamic ns, string owner, out string resolved)
        {
            dynamic recipient = ns.CreateRecipient(owner);
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

        /// <summary>Календари панели «Календарь» Outlook по группам («Мои календари», «Общие календари»…).</summary>
        /// Открытый сейчас календарь — отмеченный в панели (IsSelected): идентификатор CurrentFolder окна у общих
        /// календарей длинный и строкой не совпадает с идентификатором той же папки в панели (проверено вживую).
        private void AddNavigationPane(dynamic ns, List<OutlookCalendarRef> list)
        {
            try
            {
                dynamic explorer = _app!.ActiveExplorer();
                if (explorer is null)
                {
                    return;
                }

                dynamic module = explorer.NavigationPane.Modules.GetNavigationModule(OlModuleCalendar);
                dynamic groups = module.NavigationGroups;
                int groupCount = (int)groups.Count;
                for (int g = 1; g <= groupCount; g++)
                {
                    dynamic group = groups.Item(g);
                    string groupName = (string)group.Name;
                    dynamic folders = group.NavigationFolders;
                    int count = (int)folders.Count;
                    for (int f = 1; f <= count; f++)
                    {
                        try
                        {
                            dynamic navigation = folders.Item(f);
                            Add((object)ns, list, (string)navigation.DisplayName + " — " + groupName, navigation.Folder, (bool)navigation.IsSelected);
                        }
#pragma warning disable CA1031 // Недоступный общий календарь (нет прав, сервер владельца недоступен) просто не показывается.
                        catch (Exception)
#pragma warning restore CA1031
                        {
                        }
                    }
                }
            }
#pragma warning disable CA1031 // Нет окна Outlook или панели (старая версия) — остаются календари ящиков профиля.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        private dynamic Namespace()
        {
            if (_app is null)
            {
                try
                {
                    _app = Marshal.GetActiveObject("Outlook.Application");
                }
                catch (COMException ex)
                {
                    throw new OutlookNotRunningException(ex);
                }
            }

            return _app.GetNamespace("MAPI");
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
