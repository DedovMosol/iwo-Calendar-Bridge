using System;
using System.Linq;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Errors;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Резервный адрес сервера: правила профиля (свой сертификат, не тот же сервер, только HTTPS, отпечаток только
    /// вместе с адресом) и правило перехода — только при недоступности адреса по сети. Адреса — синтетические.
    /// </summary>
    public sealed class AlternateEndpointTests
    {
        private const string PinA = "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string PinB = "0102030405060708090A0B0C0D0E0F1011121314";

        private static readonly Uri Primary = new Uri("https://host.example.test/EWS/Exchange.asmx");
        private static readonly Uri Other = new Uri("https://mail.example.test/EWS/Exchange.asmx");

        [Fact]
        public void NoAlternate_OneEndpoint()
        {
            var profile = new ConnectionProfile(Primary, null, TimeSpan.Zero, PinA);

            ServerEndpoint endpoint = Assert.Single(profile.Endpoints);
            Assert.Equal(Primary, endpoint.EwsUrl);
            Assert.Equal(PinA, endpoint.PinnedCertificateThumbprint);
            Assert.False(endpoint.IsAlternate);
            Assert.Null(profile.AlternateEwsUrl);
        }

        [Fact]
        public void Alternate_SecondEndpoint_WithItsOwnPin()
        {
            var profile = new ConnectionProfile(
                Primary, null, TimeSpan.Zero, PinA, Other, "01:02:03:04:05:06:07:08:09:0a:0b:0c:0d:0e:0f:10:11:12:13:14");

            Assert.Equal(2, profile.Endpoints.Count);
            Assert.Equal(PinA, profile.Endpoints[0].PinnedCertificateThumbprint);
            Assert.False(profile.Endpoints[0].IsAlternate);
            Assert.Equal(Other, profile.Endpoints[1].EwsUrl);
            Assert.Equal(PinB, profile.Endpoints[1].PinnedCertificateThumbprint);
            Assert.True(profile.Endpoints[1].IsAlternate);
        }

        [Theory]
        [InlineData("https://host.example.test/EWS/Exchange.asmx")]
        [InlineData("https://HOST.example.test/other/path.asmx")] // тот же хост и порт — тот же сервер TLS
        [InlineData("https://host.example.test:443/EWS/Exchange.asmx")]
        public void Alternate_SameHostAndPort_Rejected(string alternate)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(Primary, null, TimeSpan.Zero, null, new Uri(alternate)));

            Assert.Equal("alternateEwsUrl", ex.ParamName);
        }

        [Fact]
        public void Alternate_SameHostOtherPort_Accepted()
        {
            var profile = new ConnectionProfile(Primary, null, TimeSpan.Zero, null, new Uri("https://host.example.test:8443/EWS/Exchange.asmx"));

            Assert.Equal(2, profile.Endpoints.Count);
        }

        [Fact]
        public void Alternate_Http_RejectedWithItsField()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(Primary, null, TimeSpan.Zero, null, new Uri("http://mail.example.test/EWS/Exchange.asmx")));

            Assert.Equal("alternateEwsUrl", ex.ParamName);
        }

        [Fact]
        public void AlternatePin_WithoutAlternateUrl_Rejected()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(Primary, null, TimeSpan.Zero, null, null, PinB));

            Assert.Equal("alternatePinnedCertificateThumbprint", ex.ParamName);
        }

        [Fact]
        public void AlternatePin_Malformed_RejectedWithItsField()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(Primary, null, TimeSpan.Zero, null, Other, "zz"));

            Assert.Equal("alternatePinnedCertificateThumbprint", ex.ParamName);
        }

        [Fact]
        public void ForEndpoint_SingleAddress_SameMailboxAndZone()
        {
            var profile = new ConnectionProfile(Primary, "user@example.test", TimeSpan.FromHours(3), PinA, Other, PinB);

            ConnectionProfile only = profile.ForEndpoint(profile.Endpoints[1]);

            ServerEndpoint endpoint = Assert.Single(only.Endpoints);
            Assert.Equal(Other, endpoint.EwsUrl);
            Assert.Equal(PinB, endpoint.PinnedCertificateThumbprint);
            Assert.Equal("user@example.test", only.MailboxAddress);
            Assert.Equal(TimeSpan.FromHours(3), only.TimeZone.Offset);
        }

        [Fact]
        public void EndpointKey_HostCaseInsensitive_PortExplicit()
        {
            Assert.Equal(
                ServerEndpoint.KeyOf(new Uri("https://Mail.Example.Test/EWS/Exchange.asmx")),
                ServerEndpoint.KeyOf(new Uri("https://mail.example.test:443/ews/exchange.asmx")));
            Assert.NotEqual(
                ServerEndpoint.KeyOf(Other),
                ServerEndpoint.KeyOf(new Uri("https://mail.example.test:444/EWS/Exchange.asmx")));
        }

        [Fact]
        public void Failover_OnlyWhenAddressUnreachable()
        {
            // Вход, права, сертификат, TLS, «сервер занят» — тот же сервер ответил бы так же и по другому адресу.
            OperationErrorKind[] unreachable = Enum.GetValues(typeof(OperationErrorKind)).Cast<OperationErrorKind>()
                .Where(ErrorClassifier.IsUnreachable)
                .ToArray();

            Assert.Equal(new[] { OperationErrorKind.Timeout, OperationErrorKind.ConnectionReset }, unreachable);
        }
    }
}
