using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IwoBridge.App.Configuration;
using IwoBridge.App.Copy;
using IwoBridge.Core.Sync;
using IwoBridge.Core.Time;
using IwoBridge.Wpf.Messages;
using IwoBridge.Wpf.Mvvm;
using IwoBridge.Wpf.Services;
using IwoBridge.Wpf.Text;

namespace IwoBridge.Wpf.ViewModels
{
    /// <summary>Строка предпросмотра копирования — одно действие, которое человек может снять или оставить.</summary>
    public sealed class CopyRow : ObservableObject
    {
        private bool _isSelected = true;

        internal CopyRow(SyncAction action, string kind, string subject, string start, string reason)
        {
            Action = action;
            Kind = kind;
            Subject = subject;
            Start = start;
            Reason = reason;
        }

        internal SyncAction Action { get; }

        /// <summary>Что будет сделано: «Создать», «Перезаписать», «Удалить копию»…</summary>
        public string Kind { get; }

        public string Subject { get; }

        public string Start { get; }

        public string Reason { get; }

        public bool IsDelete => Action.Kind == SyncActionKind.Delete;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>Вариант для личных событий в списке.</summary>
    public sealed class PrivacyOption
    {
        internal PrivacyOption(PrivateEvents value, string label)
        {
            Value = value;
            Label = label;
        }

        public PrivateEvents Value { get; }

        public string Label { get; }
    }

    /// <summary>Откуда копировать.</summary>
    public enum SourceKind
    {
        /// <summary>Календарь Outlook этого компьютера.</summary>
        Outlook,

        /// <summary>Сервер Exchange по EWS (отдельное подключение «Источник»).</summary>
        Ews,
    }
}
