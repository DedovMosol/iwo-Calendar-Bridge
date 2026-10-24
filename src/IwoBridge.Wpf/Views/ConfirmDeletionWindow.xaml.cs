using System;
using System.Windows;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Окно подтверждения удаления. Правило доступности кнопки — в <see cref="DeletionConfirmationViewModel"/>;
    /// здесь кнопка ещё раз сверяется с ним перед закрытием (защита от нажатия клавиатурным сокращением).
    /// </summary>
    public partial class ConfirmDeletionWindow : Window
    {
        public ConfirmDeletionWindow(DeletionConfirmationViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // Не выше экрана: при крупном масштабе текст прокручивается, кнопки остаются на месте.
            WindowSizing.FitToWorkArea(this);
        }

        private void OnConfirm(object sender, RoutedEventArgs e)
        {
            if (((DeletionConfirmationViewModel)DataContext).CanConfirm)
            {
                DialogResult = true;
            }
        }
    }
}
