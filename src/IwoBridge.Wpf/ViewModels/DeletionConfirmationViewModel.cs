using System;
using IwoBridge.Wpf.Mvvm;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>
    /// Окно подтверждения удаления. Кнопка «Удалить» доступна, только когда введена фраза подтверждения (адрес ящика
    /// или имя учётной записи — без учёта регистра и крайних пробелов), а при окончательном удалении ещё и отмечено
    /// «Понимаю, что вернуть нельзя». Так случайное нажатие Enter не удалит ничего.
    /// </summary>
    public sealed class DeletionConfirmationViewModel : ObservableObject
    {
        private string _typedPhrase = string.Empty;
        private bool _understoodIrreversible;

        public DeletionConfirmationViewModel(string targetLabel, string phrase, string summary, bool hardDelete)
        {
            TargetLabel = targetLabel ?? throw new ArgumentNullException(nameof(targetLabel));
            Phrase = phrase ?? throw new ArgumentNullException(nameof(phrase));
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            HardDelete = hardDelete;
        }

        public string TargetLabel { get; }

        /// <summary>Что нужно ввести.</summary>
        public string Phrase { get; }

        /// <summary>Что будет удалено, одной строкой: «1 234 события, 5 серий целиком и 17 экземпляров серий».</summary>
        public string Summary { get; }

        /// <summary>Окончательное удаление (без папки «Удалённые»).</summary>
        public bool HardDelete { get; }

        public string ModeText => HardDelete
            ? "Окончательно — элементы нельзя будет вернуть."
            : "В папку «Удалённые» — события можно вернуть перетаскиванием обратно в календарь. Удалённые экземпляры "
              + "серий так не вернуть.";

        public string TypedPhrase
        {
            get => _typedPhrase;
            set
            {
                if (SetProperty(ref _typedPhrase, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(CanConfirm));
                    OnPropertyChanged(nameof(PhraseMatches));
                }
            }
        }

        public bool UnderstoodIrreversible
        {
            get => _understoodIrreversible;
            set
            {
                if (SetProperty(ref _understoodIrreversible, value))
                {
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        /// <summary>Введённое совпадает с фразой (без учёта регистра и крайних пробелов) — подсветка поля в окне.</summary>
        public bool PhraseMatches => string.Equals(_typedPhrase.Trim(), Phrase.Trim(), StringComparison.OrdinalIgnoreCase);

        public bool CanConfirm => PhraseMatches && (!HardDelete || _understoodIrreversible);
    }
}
