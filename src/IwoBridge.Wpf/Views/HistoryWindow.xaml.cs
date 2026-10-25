using System;
using System.Windows;
using System.Windows.Input;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>Окно «История»: разметка — в HistoryWindow.xaml, логика — в <see cref="HistoryViewModel"/>. Окно не больше экрана.</summary>
    public partial class HistoryWindow : Window
    {
        public HistoryWindow(HistoryViewModel viewModel)
        {
            InitializeComponent();
            WindowSizing.FitToWorkArea(this);
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        protected override void OnClosed(EventArgs e)
        {
            ((HistoryViewModel)DataContext).Dispose();
            base.OnClosed(e);
        }

        private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
