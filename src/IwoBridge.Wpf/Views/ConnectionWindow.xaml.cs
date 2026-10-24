using System;
using System.ComponentModel;
using System.Windows;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Окно «Подключение». Логика — в <see cref="ConnectionViewModel"/>. Здесь только две вещи, которые WPF не умеет
    /// через привязку: передача пароля из <c>PasswordBox</c> (его содержимое намеренно не связывается с данными) и
    /// закрытие окна после сохранения. Закрыть окно во время проверки можно — проверка отменяется.
    /// </summary>
    public partial class ConnectionWindow : Window
    {
        public ConnectionWindow(ConnectionViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            viewModel.Saved += OnSaved;

            // Окно растёт по содержимому, но не выше рабочей области экрана: на 1366×768 и при масштабе 125–150 %
            // кнопки «Сохранить» и «Отмена» остаются видимыми, а лишнее прокручивается.
            WindowSizing.FitToWorkArea(this);
        }

        private ConnectionViewModel ViewModel => (ConnectionViewModel)DataContext;

        protected override void OnClosing(CancelEventArgs e)
        {
            // После закрытия ничего не сохраняется, даже если ответ сервера уже в пути.
            ViewModel.Close();
            ViewModel.Saved -= OnSaved;
            base.OnClosing(e);
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.SetPassword(PasswordInput.Password);
        }

        private void OnSaved(object? sender, EventArgs e)
        {
            DialogResult = true;
        }
    }
}
