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
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

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

    /// <summary>Категория в подсказке предпросмотра: название и образец цвета в источнике (если он известен).</summary>
    public sealed class CategoryRow
    {
        // Цвета категорий Outlook 2007–2013 (OlCategoryColor 1–25) — для образца рядом с названием. Кисти заморожены:
        // одни на все строки и потоки.
        private static readonly Brush[] Palette = new uint[]
        {
            0xE7A1A2, 0xF9BA89, 0xF7DD8F, 0xFCFA90, 0x78D168, 0x9FDCC9, 0xC6D2B0, 0x9DB7E8, 0xB5A1E2, 0xDAAEC2,
            0xDAD9DC, 0x6B7994, 0xBFBFBF, 0x6F6F6F, 0x4F4F4F, 0xC11A25, 0xE2620D, 0xC79930, 0xB9B300, 0x368F2B,
            0x329B7A, 0x778B45, 0x2858A5, 0x5C3FA3, 0x93446B,
        }.Select(rgb =>
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return (Brush)brush;
        }).ToArray();

        internal CategoryRow(EventCategory category)
        {
            Name = category.Name;
            int index = (int)(category.Color ?? CategoryColor.None) - 1;
            Swatch = index >= 0 && index < Palette.Length ? Palette[index] : null;
        }

        public string Name { get; }

        /// <summary>Образец цвета; <c>null</c> — цвет в источнике неизвестен или не задан.</summary>
        public Brush? Swatch { get; }

        public bool HasSwatch => Swatch != null;
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
