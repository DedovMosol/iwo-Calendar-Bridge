using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Вкладка копирования: выбор календаря Outlook — список, поиск общего календаря по имени, запомненный выбор.</summary>
    public sealed partial class CopyViewModel
    {
        public bool OutlookAvailable => _outlook?.IsAvailable ?? false;

        public bool UseOutlook
        {
            get => _sourceKind == SourceKind.Outlook;
            set
            {
                if (value)
                {
                    SetSourceKind(SourceKind.Outlook);
                }
            }
        }

        public bool UseEws
        {
            get => _sourceKind == SourceKind.Ews;
            set
            {
                if (value)
                {
                    SetSourceKind(SourceKind.Ews);
                }
            }
        }

        public IReadOnlyList<SourceChoice> OutlookCalendars
        {
            get => _outlookCalendars;
            private set => SetProperty(ref _outlookCalendars, value);
        }

        public SourceChoice? OutlookCalendar
        {
            get => _outlookCalendar;
            set
            {
                // Тот же календарь из перечитанного списка (новый объект с тем же идентификатором папки) — не новый выбор:
                // открытый предпросмотр остаётся.
                if (value != null && _outlookCalendar != null && value.Key == _outlookCalendar.Key)
                {
                    if (!ReferenceEquals(value, _outlookCalendar))
                    {
                        _outlookCalendar = value;
                        OnPropertyChanged(nameof(OutlookCalendar));
                    }

                    return;
                }

                if (SetProperty(ref _outlookCalendar, value))
                {
                    Invalidate();
                    Refresh();

                    // Пустой список (Outlook закрыт) выбор не стирает: календарь найдётся при следующем «Обновить».
                    // Тот же календарь после перечитывания списка — не новый выбор: файл не перезаписывается.
                    if (value != null && value.Key != _savedCalendarKey)
                    {
                        _savedCalendar = value.Label;
                        _savedCalendarKey = value.Key;
                        SaveSettings();
                    }
                }
            }
        }

        /// <summary>Что искать: ФИО владельца общего календаря или часть названия календаря Outlook.</summary>
        public string CalendarSearch
        {
            get => _calendarSearch;
            set
            {
                if (SetProperty(ref _calendarSearch, value ?? string.Empty))
                {
                    FindOutlookCalendarCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Почему список Outlook пуст (Outlook не открыт и т. п.); <c>null</c> — всё в порядке.</summary>
        public string? OutlookError
        {
            get => _outlookError;
            private set => SetProperty(ref _outlookError, value);
        }

        /// <param name="substitute">Прежний календарь не нашёлся — выбрать другой (по просьбе человека) или оставить пусто.</param>
        private async Task RefreshOutlookAsync(bool substitute)
        {
            if (_outlook is null)
            {
                return;
            }

            _isLoadingOutlook = true;
            Refresh();
            try
            {
                OutlookError = null;
                // Найденные по имени общие календари остаются в списке и после «Обновить».
                IReadOnlyList<SourceChoice> listed = await _outlook.ListAsync().ConfigureAwait(true);
                SourceChoice[] calendars = listed.Concat(_foundShared.Where(f => listed.All(c => c.Key != f.Key))).ToArray();
                OutlookCalendars = calendars;

                // Выбор: прежний — по идентификатору папки, затем по подписи (запомнен между запусками); иначе общий
                // календарь, найденный когда-то по имени владельца, — заново через адресную книгу.
                SourceChoice? chosen = calendars.FirstOrDefault(c => _savedCalendarKey != null && c.Key == _savedCalendarKey)
                    ?? calendars.FirstOrDefault(c => c.Label == _savedCalendar);
                if (chosen is null && _savedOwner != null && _foundShared.Count == 0)
                {
                    chosen = await TryFindSharedAsync(_savedOwner).ConfigureAwait(true);
                    if (chosen != null)
                    {
                        calendars = calendars.Concat(new[] { chosen }).ToArray();
                        OutlookCalendars = calendars;
                    }
                }

                // Прежнего нет: человеку (кнопка «Обновить», старт) — открытый сейчас в Outlook, иначе первый. Расписанию —
                // ничего: молча копировать другой календарь в чужой ящик нельзя.
                bool firstChoice = _savedCalendarKey is null && _savedCalendar is null && _savedOwner is null;
                OutlookCalendar = chosen
                    ?? (substitute || firstChoice ? calendars.FirstOrDefault(c => c.IsCurrent) ?? calendars.FirstOrDefault() : null);
                if (calendars.Length == 0)
                {
                    OutlookError = "В Outlook не найдено ни одного календаря.";
                }
            }
#pragma warning disable CA1031 // Outlook не открыт или занят — показать текст, окно рабочее.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OutlookCalendars = Array.Empty<SourceChoice>();
                OutlookCalendar = null;
                OutlookError = ex.Message;
            }
            finally
            {
                _isLoadingOutlook = false;
                Refresh();
            }
        }

        /// <summary>
        /// Найти календарь по введённому тексту: сначала среди календарей Outlook (часть названия, без учёта регистра и
        /// «ё»), затем — общий календарь владельца через адресную книгу Outlook. Найденный выбирается.
        /// </summary>
        private async Task FindOutlookCalendarAsync()
        {
            string text = (_calendarSearch ?? string.Empty).Trim();
            OutlookError = null;
            if (text.Length == 0)
            {
                OutlookError = "Введите ФИО владельца или часть названия календаря.";
                return;
            }

            SetSourceKind(SourceKind.Outlook);
            SourceChoice? local = _outlookCalendars.FirstOrDefault(c => Fold(c.Label).Contains(Fold(text)));
            if (local != null)
            {
                OutlookCalendar = local;
                return;
            }

            _isLoadingOutlook = true;
            Refresh();
            try
            {
                SourceChoice found = await _outlook!.FindSharedAsync(text).ConfigureAwait(true);
                _foundShared.RemoveAll(f => f.Key == found.Key);
                _foundShared.Add(found);
                _ownerOfFound = text;
                _savedOwner = text;
                OutlookCalendars = _outlookCalendars.Where(c => c.Key != found.Key).Concat(new[] { found }).ToArray();
                OutlookCalendar = found;
            }
#pragma warning disable CA1031 // Не найден, нет прав, Outlook закрыт — текст под полем, окно рабочее.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OutlookError = ex.Message;
            }
            finally
            {
                _isLoadingOutlook = false;
                Refresh();
            }
        }

        /// <summary>Общий календарь по запомненному имени владельца; не нашёлся — <c>null</c> (выбор по умолчанию).</summary>
        private async Task<SourceChoice?> TryFindSharedAsync(string owner)
        {
            try
            {
                SourceChoice found = await _outlook!.FindSharedAsync(owner).ConfigureAwait(true);
                _foundShared.Add(found);
                _ownerOfFound = owner;
                return found;
            }
#pragma warning disable CA1031 // Владельца больше нет или нет доступа — выбирается календарь по умолчанию.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }

        private static string Fold(string text) => text.ToLower(RussianText.Culture).Replace('ё', 'е');
    }
}
