using System;
using IwoBridge.Core.Configuration;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Поле «Сервер»: любой вариант ввода (имя, IP, порт, полный адрес, внутренний или внешний) даёт адрес EWS;
    /// небезопасный http и мусор отклоняются с понятным текстом. Адреса синтетические (example.test, частные IP).
    /// </summary>
    public sealed class ServerAddressTests
    {
        [Theory]
        [InlineData("mail.example.test", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("  mail.example.test  ", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("10.0.0.5", "https://10.0.0.5/EWS/Exchange.asmx")]
        [InlineData("[fd00::5]", "https://[fd00::5]/EWS/Exchange.asmx")]
        [InlineData("mail.example.test:8443", "https://mail.example.test:8443/EWS/Exchange.asmx")]
        [InlineData("https://mail.example.test", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("https://mail.example.test/", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("https://mail.example.test/ews", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("https://mail.example.test/EWS/", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("https://mail.example.test/EWS/Exchange.asmx", "https://mail.example.test/EWS/Exchange.asmx")]
        [InlineData("HTTPS://MAIL.example.test/ews/exchange.asmx", "https://mail.example.test/ews/exchange.asmx")]
        [InlineData("https://mail.example.test/custom/ews.asmx", "https://mail.example.test/custom/ews.asmx")]
        public void TryParse_AcceptsAnyFormOfAddress(string input, string expected)
        {
            Assert.True(ServerAddress.TryParse(input, out Uri? url, out string? error), error);
            Assert.Equal(new Uri(expected), url);
            Assert.Null(error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://mail.example.test")]
        [InlineData("ftp://mail.example.test")]
        [InlineData("mail example test")]
        [InlineData("https://")]
        [InlineData("user:pass@mail.example.test")]
        [InlineData("https://user@mail.example.test/EWS/Exchange.asmx")]
        [InlineData("mail.example.test@evil.example.test")]
        [InlineData("010.0.0.5")]
        [InlineData("10.5")]
        [InlineData("mail\u00A0example.test")]
        [InlineData("mail.example.test\u200E")]
        [InlineData("https://mail.example.test:0/")]
        [InlineData("mail.example.test:99999")]
        [InlineData("https:/mail.example.test")]
        [InlineData("https://mail.example.test/EWS/Exchange.asmx#x")]
        public void TryParse_Rejects_WithMessage(string? input)
        {
            Assert.False(ServerAddress.TryParse(input, out Uri? url, out string? error));
            Assert.Null(url);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Theory]
        [InlineData("user:secretpass@mail.example.test")]
        [InlineData("010.0.0.5")]
        public void TryParse_ErrorNeverEchoesInput(string input)
        {
            ServerAddress.TryParse(input, out _, out string? error);

            Assert.DoesNotContain("secretpass", error);
            Assert.DoesNotContain("010.0.0.5", error);
        }

        [Fact]
        public void Validate_SameRuleForProfileFile()
        {
            // Файл настроек проверяется тем же правилом, что и поле «Сервер» (DRY).
            Assert.Throws<ArgumentException>(() => new ConnectionProfile(
                new Uri("https://user:pass@mail.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null));
            Assert.Null(ServerAddress.Validate(new Uri("https://mail.example.test/EWS/Exchange.asmx")));
        }

        [Fact]
        public void TryParse_Http_ExplainsWhy()
        {
            ServerAddress.TryParse("http://mail.example.test", out _, out string? error);

            Assert.Contains("https://", error);
        }

        [Theory]
        [InlineData("https://mail.example.test/EWS/Exchange.asmx", "mail.example.test")]
        [InlineData("https://mail.example.test:8443/EWS/Exchange.asmx", "mail.example.test:8443")]
        [InlineData("https://10.0.0.5/EWS/Exchange.asmx", "10.0.0.5")]
        [InlineData("https://mail.example.test/custom/ews.asmx", "https://mail.example.test/custom/ews.asmx")]
        public void ToDisplay_ShortForStandardPath(string url, string expected)
        {
            Assert.Equal(expected, ServerAddress.ToDisplay(new Uri(url)));
        }

        [Theory]
        [InlineData("mail.example.test")]
        [InlineData("mail.example.test:8443")]
        [InlineData("https://mail.example.test/custom/ews.asmx")]
        public void DisplayThenParse_RoundTrip(string input)
        {
            Assert.True(ServerAddress.TryParse(input, out Uri? url, out _));
            Assert.True(ServerAddress.TryParse(ServerAddress.ToDisplay(url!), out Uri? again, out _));
            Assert.Equal(url, again);
        }
    }
}
