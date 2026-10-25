using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IwoBridge.Ews.Mapping;
using Microsoft.Exchange.WebServices.Data;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Канал EWS: переход на резервный адрес. Чтение переходит при любой недоступности; неидемпотентная запись — только
    /// когда запрос точно не ушёл (иначе повтор на другом адресе создал бы второй экземпляр). Сеть не используется.
    /// </summary>
    public sealed class EwsChannelTests
    {
        private static ExchangeService[] TwoAddresses() => new[]
        {
            new ExchangeService(ExchangeVersion.Exchange2007_SP1),
            new ExchangeService(ExchangeVersion.Exchange2007_SP1),
        };

        private static Exception Wrapped(WebExceptionStatus status) =>
            new ServiceRequestException("The request failed.", new WebException("сбой", status));

        [Fact]
        public async Task Read_Timeout_FailsOverToAlternate()
        {
            ExchangeService[] services = TwoAddresses();
            var channel = new EwsChannel(services, null);
            var used = new List<ExchangeService>();

            int result = await channel.RunAsync(
                s =>
                {
                    used.Add(s);
                    return ReferenceEquals(s, services[0]) ? throw Wrapped(WebExceptionStatus.Timeout) : 1;
                },
                CancellationToken.None);

            Assert.Equal(1, result);
            Assert.Equal(new[] { services[0], services[1] }, used);
            Assert.Equal(1, channel.ActiveIndex);
        }

        [Theory]
        [InlineData(WebExceptionStatus.Timeout)]
        [InlineData(WebExceptionStatus.ReceiveFailure)]
        [InlineData(WebExceptionStatus.ConnectionClosed)]
        public async Task NonIdempotent_ResponseLost_NotRepeatedElsewhere(WebExceptionStatus status)
        {
            ExchangeService[] services = TwoAddresses();
            var channel = new EwsChannel(services, null);
            int calls = 0;

            await Assert.ThrowsAsync<ServiceRequestException>(() => channel.RunOnceNonIdempotentAsync<int>(
                s =>
                {
                    calls++;
                    throw Wrapped(status);
                },
                CancellationToken.None));

            Assert.Equal(1, calls);
            Assert.Equal(0, channel.ActiveIndex);
        }

        [Theory]
        [InlineData(WebExceptionStatus.ConnectFailure)]
        [InlineData(WebExceptionStatus.NameResolutionFailure)]
        public async Task NonIdempotent_NotSent_GoesToAlternate(WebExceptionStatus status)
        {
            ExchangeService[] services = TwoAddresses();
            var channel = new EwsChannel(services, null);

            int result = await channel.RunOnceNonIdempotentAsync(
                s => ReferenceEquals(s, services[0]) ? throw Wrapped(status) : 7,
                CancellationToken.None);

            Assert.Equal(7, result);
            Assert.Equal(1, channel.ActiveIndex);
        }

        [Fact]
        public async Task SharedEndpoint_SecondChannelStartsOnWorkingAddress()
        {
            var active = new ActiveEndpoint();
            ExchangeService[] readServices = TwoAddresses();
            ExchangeService[] writeServices = TwoAddresses();
            var read = new EwsChannel(readServices, null, active);
            var write = new EwsChannel(writeServices, null, active);
            await read.RunAsync(s => ReferenceEquals(s, readServices[0]) ? throw Wrapped(WebExceptionStatus.ConnectFailure) : 0, CancellationToken.None);

            ExchangeService? used = null;
            await write.RunAsync(s => { used = s; return 0; }, CancellationToken.None);

            Assert.Same(writeServices[1], used);
        }

        [Fact]
        public void IsNotSent_RecognizesSocketRefusal_NotTimeout()
        {
            Assert.True(EwsErrorMapper.IsNotSent(new ServiceRequestException("x", new WebException("x", new SocketException((int)SocketError.ConnectionRefused), WebExceptionStatus.UnknownError, null))));
            Assert.False(EwsErrorMapper.IsNotSent(new TimeoutException()));
            Assert.False(EwsErrorMapper.IsNotSent(Wrapped(WebExceptionStatus.SendFailure)));
        }
    }
}
