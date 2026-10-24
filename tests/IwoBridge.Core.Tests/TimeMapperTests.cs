using System;
using IwoBridge.Core.Time;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Нормализация времени к UTC по DateTime.Kind: Utc — как есть, Local — конвертируется, Unspecified — считается UTC (как в EWS).
    /// </summary>
    public sealed class TimeMapperTests
    {
        [Fact]
        public void ToUtc_Utc_ReturnsSameInstant()
        {
            DateTime value = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            DateTime result = TimeMapper.ToUtc(value);

            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(value, result);
        }

        [Fact]
        public void ToUtc_Unspecified_TreatedAsUtc()
        {
            DateTime value = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

            DateTime result = TimeMapper.ToUtc(value);

            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(value.Ticks, result.Ticks);
        }

        [Fact]
        public void ToUtc_Local_ConvertedToUtc()
        {
            DateTime value = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

            DateTime result = TimeMapper.ToUtc(value);

            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(value.ToUniversalTime(), result);
        }
    }
}
