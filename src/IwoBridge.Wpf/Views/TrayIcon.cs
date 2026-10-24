using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using IwoBridge.Wpf.ViewModels;
using Forms = System.Windows.Forms;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Значок в области уведомлений, пока включено автокопирование: окно можно закрыть, а программа продолжит копировать
    /// по расписанию. Двойной щелчок — открыть окно; меню — «Открыть», «Скопировать сейчас», «Справка», «Выход».
    /// Уведомление показывается только о проблемах автокопирования и один раз — при первом уходе в область уведомлений.
    ///
    /// Значок WinForms (в WPF своего нет). Всё, что он держит (значок, меню, подписки), освобождается в Dispose —
    /// после выхода в области уведомлений не остаётся «мёртвого» значка, а модель не удерживается событиями.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int BalloonMilliseconds = 8000;

        private readonly MainViewModel _model;
        private readonly Action _show;
        private readonly Action _exit;
        private readonly Forms.NotifyIcon _icon;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly System.Drawing.Icon _image;
        private bool _hintShown;

        public TrayIcon(MainViewModel model, Action show, Action exit)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _show = show ?? throw new ArgumentNullException(nameof(show));
            _exit = exit ?? throw new ArgumentNullException(nameof(exit));

            using (Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/IwoCalendarBridge;component/app.ico")).Stream)
            {
                _image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }

            _menu = new Forms.ContextMenuStrip();
            _menu.Items.Add("Открыть", null, (_, __) => _show());
            _menu.Items.Add("Скопировать сейчас", null, (_, __) => _model.Copy.Auto.RunNowCommand.Execute(null));
            _menu.Items.Add("Справка", null, (_, __) => _model.HelpCommand.Execute(null));
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add("Выход", null, (_, __) => _exit());

            _icon = new Forms.NotifyIcon { Icon = _image, ContextMenuStrip = _menu, Text = "iwo Calendar Bridge" };
            _icon.DoubleClick += OnDoubleClick;
            _model.Copy.Auto.PropertyChanged += OnAutoChanged;
            _model.Copy.Auto.Problem += OnProblem;
            Update();
        }

        /// <summary>Окно ушло в область уведомлений — один раз подсказать, где теперь программа.</summary>
        public void ShowHiddenHint()
        {
            if (_hintShown || !_icon.Visible)
            {
                return;
            }

            _hintShown = true;
            _icon.ShowBalloonTip(
                BalloonMilliseconds,
                "Программа продолжает работать",
                "Автокопирование идёт по расписанию. Открыть — двойной щелчок по значку, выйти — правой кнопкой → «Выход».",
                Forms.ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _model.Copy.Auto.PropertyChanged -= OnAutoChanged;
            _model.Copy.Auto.Problem -= OnProblem;
            _icon.DoubleClick -= OnDoubleClick;
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _image.Dispose();
        }

        private void OnDoubleClick(object? sender, EventArgs e) => _show();

        private void OnAutoChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AutoCopyViewModel.IsEnabled) || e.PropertyName == nameof(AutoCopyViewModel.StatusText))
            {
                Update();
            }
        }

        private void OnProblem(object? sender, AutoRunOutcome outcome)
        {
            if (_icon.Visible)
            {
                _icon.ShowBalloonTip(BalloonMilliseconds, "Автокопирование: нужна проверка", outcome.Summary, Forms.ToolTipIcon.Warning);
            }
        }

        private void Update()
        {
            AutoCopyViewModel auto = _model.Copy.Auto;
            _icon.Visible = auto.IsEnabled;

            // Подсказка значка — не длиннее 63 символов (ограничение Windows).
            string text = auto.IsEnabled ? "iwo Calendar Bridge — раз в " + auto.Interval.Label : "iwo Calendar Bridge";
            _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
    }
}
