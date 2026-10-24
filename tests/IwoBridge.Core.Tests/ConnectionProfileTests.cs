using System;
using IwoBridge.Core.Configuration;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>Правила профиля подключения. Адреса — только синтетические домены <c>example.test</c> (T36).</summary>
    public sealed class ConnectionProfileTests
    {
        private static readonly Uri ValidUrl = new Uri("https://host.example.test/EWS/Exchange.asmx");

        [Fact]
        public void Constructor_NonHttps_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(new Uri("http://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null));
        }

        [Fact]
        public void Constructor_RelativeUri_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(new Uri("/EWS/Exchange.asmx", UriKind.Relative), null, TimeSpan.Zero, null));
        }

        [Fact]
        public void Constructor_NullMailbox_StaysNull()
        {
            var profile = new ConnectionProfile(ValidUrl, null, TimeSpan.FromHours(3), null);

            Assert.Null(profile.MailboxAddress);
            Assert.Equal(TimeSpan.FromHours(3), profile.TimeZone.Offset);
            Assert.Null(profile.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Constructor_TrimsMailbox()
        {
            var profile = new ConnectionProfile(ValidUrl, "  user@example.test  ", TimeSpan.Zero, null);

            Assert.Equal("user@example.test", profile.MailboxAddress);
        }

        [Fact]
        public void Constructor_NormalizesThumbprint_IncludingInvisibleMark()
        {
            var profile = new ConnectionProfile(
                ValidUrl, null, TimeSpan.Zero, "\u200Ea1:b2:c3:d4:e5:f6:07:18:29:30:3a:4b:5c:6d:7e:8f:90:10:20:30");

            Assert.Equal("A1B2C3D4E5F6071829303A4B5C6D7E8F90102030", profile.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Constructor_BlankThumbprint_BecomesNull()
        {
            var profile = new ConnectionProfile(ValidUrl, null, TimeSpan.Zero, "   ");

            Assert.Null(profile.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Constructor_InvalidThumbprint_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(ValidUrl, null, TimeSpan.Zero, "not-a-thumbprint"));
        }

        [Theory]
        [InlineData("user")]
        [InlineData("@example.test")]
        [InlineData("user@")]
        [InlineData("a@b@example.test")]
        [InlineData("user name@example.test")]
        [InlineData("Имя <user@example.test>")]
        [InlineData("user@example.test\u200E")]
        [InlineData("a@example.test,b@example.test")]
        public void Constructor_MalformedMailbox_Throws(string mailbox)
        {
            Assert.Throws<ArgumentException>(() => new ConnectionProfile(ValidUrl, mailbox, TimeSpan.Zero, null));
        }

        [Fact]
        public void Constructor_BlankMailbox_BecomesNull()
        {
            Assert.Null(new ConnectionProfile(ValidUrl, "   ", TimeSpan.Zero, null).MailboxAddress);
        }

        // ParamName = поле файла настроек: по нему загрузчик профиля говорит пользователю, что исправить.
        // Переименование параметра без правки загрузчика роняет эти тесты.

        [Fact]
        public void ParamName_NonHttpsUrl_IsEwsUrl()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(new Uri("http://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null));

            Assert.Equal("ewsUrl", ex.ParamName);
        }

        [Fact]
        public void ParamName_MalformedMailbox_IsMailboxAddress()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => new ConnectionProfile(ValidUrl, "user", TimeSpan.Zero, null));

            Assert.Equal("mailboxAddress", ex.ParamName);
        }

        [Theory]
        [InlineData(15 * 60)]
        [InlineData(90)]
        public void ParamName_InvalidOffset_IsUtcOffset(int minutes)
        {
            TimeSpan offset = minutes == 90 ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(minutes);

            ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => new ConnectionProfile(ValidUrl, null, offset, null));

            Assert.Equal("utcOffset", ex.ParamName);
        }

        [Fact]
        public void ParamName_InvalidThumbprint_IsPinnedCertificateThumbprint()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new ConnectionProfile(ValidUrl, null, TimeSpan.Zero, "not-a-thumbprint"));

            Assert.Equal("pinnedCertificateThumbprint", ex.ParamName);
        }
    }
}
