using System;

namespace IwoBridge.App
{
    /// <summary>
    /// Синхронный переходник сообщений о ходе между этапами сценария (очистка, копирование). <c>Progress&lt;T&gt;</c>
    /// здесь не годится: без контекста синхронизации он доставляет сообщения из пула потоков и может переставить их.
    /// Переставлять в поток окна — дело окна.
    /// </summary>
    internal sealed class MappedProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public MappedProgress(Action<T> report)
        {
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
