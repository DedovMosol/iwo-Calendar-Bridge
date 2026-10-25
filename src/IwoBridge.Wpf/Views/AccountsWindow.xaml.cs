using System;
using System.Windows;
using System.Windows.Input;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>Окно «Учётные записи»: разметка — в AccountsWindow.xaml, логика — в <see cref="AccountsViewModel"/>. Окно не больше экрана.</summary>
    public partial class AccountsWindow : Window
    {
        public AccountsWindow(AccountsViewModel viewModel)
        {
            InitializeComponent();
            WindowSizing.FitToWorkArea(this);
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
