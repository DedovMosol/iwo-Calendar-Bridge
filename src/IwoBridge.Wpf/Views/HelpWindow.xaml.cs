using System.Windows;
using System.Windows.Input;

namespace IwoBridge.Wpf.Views
{
    /// <summary>Справка: текст — в разметке (HelpWindow.xaml); логики нет. Окно не больше экрана (WindowSizing).</summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            WindowSizing.FitToWorkArea(this);
        }

        private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
    }
}
