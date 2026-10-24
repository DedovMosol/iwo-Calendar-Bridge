using System;
using System.IO;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Резервный адрес в файле настроек: поля необязательны (старые файлы читаются как раньше), запись и чтение
    /// сохраняют адрес и его собственный отпечаток, ошибка называет поле резервного адреса.
    /// </summary>
    public sealed class ConnectionProfileFileAlternateTests : IDisposable
    {
        private const string PinA = "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string PinB = "0102030405060708090A0B0C0D0E0F1011121314";

        private readonly string _folder = Path.Combine(Path.GetTempPath(), "iwo-alt-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }

        [Fact]
        public void OldFile_WithoutAlternateFields_LoadsWithoutAlternate()
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\", \"pinnedCertificateThumbprint\": null }");

            Assert.Null(profile.AlternateEwsUrl);
            Assert.Single(profile.Endpoints);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("\"   \"")]
        public void EmptyAlternate_MeansNone(string value)
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\", \"alternateEwsUrl\": " + value + " }");

            Assert.Null(profile.AlternateEwsUrl);
        }

        [Fact]
        public void SaveLoad_RoundTrip_KeepsAlternateAndItsPin()
        {
            string path = Path.Combine(_folder, "connection.json");
            var profile = new ConnectionProfile(
                new Uri("https://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.FromHours(3), PinA,
                new Uri("https://mail.example.test:8443/EWS/Exchange.asmx"), PinB);

            ConnectionProfileFile.Save(path, profile);
            ConnectionProfile loaded = ConnectionProfileFile.Load(path);

            Assert.Equal(new Uri("https://mail.example.test:8443/EWS/Exchange.asmx"), loaded.AlternateEwsUrl);
            Assert.Equal(PinA, loaded.PinnedCertificateThumbprint);
            Assert.Equal(PinB, loaded.AlternatePinnedCertificateThumbprint);
        }

        [Fact]
        public void Save_WithoutAlternate_WritesNullFields()
        {
            string text = ConnectionProfileFile.ToJson(
                new ConnectionProfile(new Uri("https://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null));

            Assert.Contains("\"alternateEwsUrl\": null", text);
            Assert.Contains("\"alternatePinnedCertificateThumbprint\": null", text);
        }

        [Theory]
        [InlineData("\"http://mail.example.test/EWS/Exchange.asmx\"")] // не HTTPS
        [InlineData("\"https://<host>/EWS/Exchange.asmx\"")] // шаблон
        [InlineData("\"not a url\"")]
        [InlineData("\"https://host.example.test/EWS/Exchange.asmx\"")] // совпадает с основным
        public void BadAlternate_ErrorNamesAlternateField(string value)
        {
            ProfileLoadException ex = Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\", \"alternateEwsUrl\": " + value + " }"));

            Assert.Equal("alternateEwsUrl", ex.FieldName);
        }

        [Fact]
        public void AlternatePin_WithoutAlternate_ErrorNamesPinField()
        {
            ProfileLoadException ex = Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\", "
                + "\"alternatePinnedCertificateThumbprint\": \"" + PinB + "\" }"));

            Assert.Equal("alternatePinnedCertificateThumbprint", ex.FieldName);
        }
    }
}
