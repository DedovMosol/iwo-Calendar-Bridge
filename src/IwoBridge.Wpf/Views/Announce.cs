using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Озвучиваемый текст для экранного диктора (Экранный диктор, NVDA, JAWS): <c>views:Announce.Text="{Binding …}"</c>
    /// на TextBlock вместо <c>Text</c>. При каждом новом непустом значении текст ставится в TextBlock и элемент
    /// сообщает UI Automation «живая область изменилась» — диктор читает его, не перемещая фокус. Насколько настойчиво
    /// — задаёт <c>AutomationProperties.LiveSetting</c> (Polite — после текущей фразы, Assertive — сразу); не задано —
    /// Polite.
    ///
    /// Зачем отдельное свойство: WPF не сообщает об изменении текста сам — LiveSetting без события
    /// LiveRegionChanged бесполезен (так рекомендует документация .NET 4.7.1+). Подписка на изменения Text через
    /// DependencyPropertyDescriptor держала бы элемент в памяти; здесь событие идёт из обработчика самого свойства —
    /// утечек нет. Применяется к заголовкам сообщений (ошибка, итог, результат проверки), а не к счётчику хода работы:
    /// диктор не должен зачитывать каждое «Удаляю: 1 200 из 2 400».
    /// В Windows 7 события живых областей система не поддерживает — вызов безвреден, текст просто показывается.
    /// </summary>
    public static class Announce
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Announce), new PropertyMetadata(null, OnTextChanged));

        public static string? GetText(DependencyObject element)
        {
            return (string?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(TextProperty);
        }

        public static void SetText(DependencyObject element, string? value)
        {
            (element ?? throw new ArgumentNullException(nameof(element))).SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (!(element is TextBlock block))
            {
                return;
            }

            string text = e.NewValue as string ?? string.Empty;
            block.Text = text;
            if (text.Length == 0)
            {
                return;
            }

            if (AutomationProperties.GetLiveSetting(block) == AutomationLiveSetting.Off)
            {
                AutomationProperties.SetLiveSetting(block, AutomationLiveSetting.Polite);
            }

            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                return;
            }

            // После разметки: сообщение часто появляется вместе с самой полосой (была скрыта) — скрытый элемент
            // диктор не читает.
            block.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (block.IsVisible && string.Equals(block.Text, text, StringComparison.Ordinal))
                {
                    AutomationPeer? peer = UIElementAutomationPeer.FromElement(block) ?? UIElementAutomationPeer.CreatePeerForElement(block);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            }));
        }
    }
}
