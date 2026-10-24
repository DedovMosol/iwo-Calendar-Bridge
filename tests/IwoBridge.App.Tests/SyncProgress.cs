using System;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Синхронный приёмник сообщений о ходе для тестов: сообщения приходят сразу и по порядку
    /// (<c>Progress&lt;T&gt;</c> без контекста синхронизации публикует в пул потоков и может их переставить).
    /// </summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
