using System;

namespace IwoBridge.Core.Paging
{
    /// <summary>Итог проверки полноты: полный результат или причина неполноты.</summary>
    public readonly struct Completeness
    {
        private Completeness(bool isComplete, string? reason)
        {
            IsComplete = isComplete;
            Reason = reason;
        }

        public bool IsComplete { get; }

        /// <summary>Причина неполноты (при <see cref="IsComplete"/> == false).</summary>
        public string? Reason { get; }

        public static Completeness Complete()
        {
            return new Completeness(true, null);
        }

        public static Completeness Incomplete(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Нужна причина неполноты.", nameof(reason));
            }

            return new Completeness(false, reason);
        }
    }
}
