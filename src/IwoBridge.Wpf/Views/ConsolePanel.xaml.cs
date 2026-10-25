using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using IwoBridge.App.History;
using IwoBridge.Wpf.ViewModels;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// Панель консоли: строки модели (<see cref="ConsoleViewModel.Lines"/>) — абзацами документа с цветом по виду
    /// строки. Добавленная строка дописывается в конец, ушедшая старая — убирается сверху; документ целиком
    /// перестраивается только при очистке. Прокрутка следует за новыми строками, если человек не листает выше.
    /// </summary>
    public partial class ConsolePanel : UserControl
    {
        private ConsoleViewModel? _model;

        public ConsolePanel()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Attach(DataContext as ConsoleViewModel);
            Unloaded += (_, __) => Attach(null);
            Loaded += (_, __) => Attach(DataContext as ConsoleViewModel);
        }

        private FlowDocument Document => Output.Document;

        private void Attach(ConsoleViewModel? model)
        {
            if (ReferenceEquals(model, _model))
            {
                return;
            }

            if (_model != null)
            {
                _model.Lines.CollectionChanged -= OnLinesChanged;
            }

            _model = model;
            if (_model != null)
            {
                _model.Lines.CollectionChanged += OnLinesChanged;
            }

            Rebuild();
        }

        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            bool atEnd = Output.VerticalOffset + Output.ViewportHeight >= Output.ExtentHeight - 4;
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewStartingIndex == Document.Blocks.Count)
            {
                foreach (OperationLine line in e.NewItems)
                {
                    Document.Blocks.Add(Paragraph(line));
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == 0 && e.OldItems?.Count == 1 && Document.Blocks.Count > 0)
            {
                Document.Blocks.Remove(Document.Blocks.FirstBlock);
            }
            else
            {
                Rebuild();
                return;
            }

            if (atEnd)
            {
                Output.ScrollToEnd();
            }
        }

        private void Rebuild()
        {
            Document.Blocks.Clear();
            if (_model != null)
            {
                foreach (OperationLine line in _model.Lines)
                {
                    Document.Blocks.Add(Paragraph(line));
                }
            }

            Output.ScrollToEnd();
        }

        /// <summary>Строка — абзацем: перевод строки внутри текста — разрыв строки, цвет — по виду (кисти палитры).</summary>
        private static Paragraph Paragraph(OperationLine line)
        {
            var paragraph = new Paragraph { Margin = new Thickness(line.Kind == OperationLineKind.Detail ? 28 : 0, 0, 0, 1) };
            string[] parts = line.Text.Trim('\r').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                paragraph.Inlines.Add(new Run(line.Kind == OperationLineKind.Detail ? parts[i].Trim() : parts[i].TrimEnd()));
            }

            paragraph.SetResourceReference(TextElement.ForegroundProperty, BrushOf(line.Kind));
            return paragraph;
        }

        private static string BrushOf(OperationLineKind kind)
        {
            switch (kind)
            {
                case OperationLineKind.Success:
                    return "ConsoleSuccessBrush";
                case OperationLineKind.Problem:
                    return "ConsoleProblemBrush";
                case OperationLineKind.Detail:
                case OperationLineKind.Cancelled:
                    return "ConsoleMutedBrush";
                default:
                    return "ConsoleTextBrush";
            }
        }

        private void OnCopyAll(object sender, RoutedEventArgs e)
        {
            if (_model is null)
            {
                return;
            }

            try
            {
                Clipboard.SetText(_model.AllText);
            }
            catch (ExternalException)
            {
                // Буфер обмена занят другой программой — человек нажмёт ещё раз.
            }
        }
    }
}
