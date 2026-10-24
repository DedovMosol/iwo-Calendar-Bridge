using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IwoBridge.Wpf.Mvvm
{
    /// <summary>
    /// Основа моделей представления: уведомление окна об изменении свойств (<see cref="INotifyPropertyChanged"/>).
    /// Без типов WPF — модели представления тестируются без окна и без диспетчера.
    ///
    /// Пример свойства: <c>public string Name { get => _name; set => SetProperty(ref _name, value); }</c>.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Присваивает значение и уведомляет окно, если оно изменилось. Возвращает true при изменении.</summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
