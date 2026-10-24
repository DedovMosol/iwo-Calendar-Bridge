using System;
using System.Windows;

namespace IwoBridge.Wpf.Views
{
    /// <summary>Вопрос с двумя действиями (<see cref="Services.IDialogService.Ask"/>); логики нет — только тексты.</summary>
    public partial class QuestionWindow : Window
    {
        public QuestionWindow(string title, string question, string confirm, string cancel)
        {
            InitializeComponent();
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Heading.Text = title;
            Message.Text = question ?? throw new ArgumentNullException(nameof(question));
            ConfirmButton.Content = confirm ?? throw new ArgumentNullException(nameof(confirm));
            CancelButton.Content = cancel ?? throw new ArgumentNullException(nameof(cancel));
            WindowSizing.FitToWorkArea(this);
        }

        private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
