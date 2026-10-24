using System;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Model
{
    /// <summary>Интервал одного экземпляра серии (например, изменённого повторения), время в UTC.</summary>
    public sealed class OccurrenceSpan
    {
        public OccurrenceSpan(DateTime startUtc, DateTime endUtc)
        {
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
        }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }
    }
}
