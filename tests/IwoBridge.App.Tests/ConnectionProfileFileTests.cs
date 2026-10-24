using System;
using System.IO;
using System.Text;
using IwoBridge.App.Configuration;
using IwoBridge.Core.Configuration;
using Xunit;

namespace IwoBridge.App.Tests
{
    /// <summary>
    /// Загрузчик файла настроек <c>config\connection.json</c>: разбор JSON, понятные ошибки с именем поля, отсутствие
    /// значений в тексте ошибок и контракт с поставляемым шаблоном. Данные — только синтетические.
    /// </summary>
    public sealed class ConnectionProfileFileTests : IDisposable
    {
        private const string ValidJson =
            "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"mailboxAddress\": \"user@example.test\", "
            + "\"utcOffset\": \"+05:30\", \"pinnedCertificateThumbprint\": null }";

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "iwo-profile-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static string TemplatePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "connection.example.json");

        private static string Json(string ewsUrl = "\"https://host.example.test/EWS/Exchange.asmx\"", string utcOffset = "\"+03:00\"",
            string mailbox = "null", string thumbprint = "null")
        {
            return "{ \"ewsUrl\": " + ewsUrl + ", \"mailboxAddress\": " + mailbox + ", \"utcOffset\": " + utcOffset
                + ", \"pinnedCertificateThumbprint\": " + thumbprint + " }";
        }

        private static ProfileLoadException ParseFails(string json) =>
            Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Parse(json));

        private string WriteFile(string content, Encoding encoding)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "connection.json");
            File.WriteAllText(path, content, encoding);
            return path;
        }

        // ---- Разбор ----

        [Fact]
        public void Parse_Valid_AllFields()
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(ValidJson);

            Assert.Equal(new Uri("https://host.example.test/EWS/Exchange.asmx"), profile.EwsUrl);
            Assert.Equal("user@example.test", profile.MailboxAddress);
            Assert.Equal(new TimeSpan(5, 30, 0), profile.TimeZone.Offset);
            Assert.Null(profile.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Parse_OptionalFieldsMissing_AreNull()
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+00:00\" }");

            Assert.Null(profile.MailboxAddress);
            Assert.Null(profile.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Parse_UnknownFieldsIncludingNestedObjects_Ignored()
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(
                "{ \"_comment\": \"x\", \"_fields\": { \"a\": [1, 2, { \"b\": null }] }, \"future\": true, "
                + "\"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"-05:00\" }");

            Assert.Equal(TimeSpan.FromHours(-5), profile.TimeZone.Offset);
        }

        [Fact]
        public void Parse_LeadingBomInString_Accepted()
        {
            Assert.NotNull(ConnectionProfileFile.Parse("\uFEFF" + ValidJson));
        }

        [Theory]
        [InlineData("+00:00", 0)]
        [InlineData("-00:00", 0)]
        [InlineData("+03:00", 180)]
        [InlineData("+05:45", 345)]
        [InlineData("-09:30", -570)]
        [InlineData("+14:00", 840)]
        [InlineData("-14:00", -840)]
        [InlineData(" +03:00 ", 180)]
        public void Parse_UtcOffset_Valid(string offset, int expectedMinutes)
        {
            ConnectionProfile profile = ConnectionProfileFile.Parse(Json(utcOffset: "\"" + offset + "\""));

            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), profile.TimeZone.Offset);
        }

        [Theory]
        [InlineData("\"03:00\"")]
        [InlineData("\"+3:00\"")]
        [InlineData("\"+03\"")]
        [InlineData("\"+0300\"")]
        [InlineData("\"+03:60\"")]
        [InlineData("\"+03:00:00\"")]
        [InlineData("\"UTC+3\"")]
        [InlineData("\"+\uFF10\uFF13:00\"")]
        [InlineData("\"+15:00\"")]
        [InlineData("\"-14:01\"")]
        [InlineData("\"\"")]
        [InlineData("null")]
        [InlineData("3")]
        public void Parse_UtcOffset_Invalid_FieldNamed(string offset)
        {
            ProfileLoadException ex = ParseFails(Json(utcOffset: offset));

            Assert.Equal("utcOffset", ex.FieldName);
            Assert.Contains("utcOffset", ex.Message);
        }

        [Fact]
        public void Parse_UtcOffsetMissing_FieldNamed_NoDefault()
        {
            // Пояс не подставляется по умолчанию: ошибка в нём сдвинула бы период очистки.
            ProfileLoadException ex = ParseFails("{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\" }");

            Assert.Equal("utcOffset", ex.FieldName);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("\"   \"")]
        [InlineData("\"EWS/Exchange.asmx\"")]
        [InlineData("\"http://host.example.test/EWS/Exchange.asmx\"")]
        public void Parse_EwsUrl_Invalid_FieldNamed(string url)
        {
            ProfileLoadException ex = ParseFails(Json(ewsUrl: url));

            Assert.Equal("ewsUrl", ex.FieldName);
        }

        [Fact]
        public void Parse_EwsUrl_TemplatePlaceholder_ExplainsWhatToDo()
        {
            ProfileLoadException ex = ParseFails(Json(ewsUrl: "\"https://<host>/EWS/Exchange.asmx\""));

            Assert.Equal("ewsUrl", ex.FieldName);
            Assert.Contains("<host>", ex.Message);
        }

        [Fact]
        public void Parse_MalformedMailbox_FieldNamed_ValueNotInMessage()
        {
            ProfileLoadException ex = ParseFails(Json(mailbox: "\"secretvalue\""));

            Assert.Equal("mailboxAddress", ex.FieldName);
            Assert.DoesNotContain("secretvalue", ex.Message);
        }

        [Fact]
        public void Parse_InvalidThumbprint_FieldNamed_ValueNotInMessage()
        {
            ProfileLoadException ex = ParseFails(Json(thumbprint: "\"secretvalue\""));

            Assert.Equal("pinnedCertificateThumbprint", ex.FieldName);
            Assert.DoesNotContain("secretvalue", ex.Message);
        }

        [Fact]
        public void Parse_InvalidOffset_ValueNotInMessage()
        {
            // У ArgumentOutOfRangeException .NET Framework дописывает «Фактическое значение…» второй строкой.
            ProfileLoadException ex = ParseFails(Json(utcOffset: "\"+15:00\""));

            Assert.DoesNotContain("15:00", ex.Message);
            Assert.DoesNotContain("\n", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("{ \"ewsUrl\": ")]
        [InlineData("{ // комментарий\n \"utcOffset\": \"+03:00\" }")]
        [InlineData("{ /* комментарий */ \"utcOffset\": \"+03:00\" }")]
        [InlineData(" [ { \"utcOffset\": \"+03:00\" } ]")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("\"text\"")]
        public void Parse_NotAProfileObject_Fails_WithoutField(string json)
        {
            ProfileLoadException ex = ParseFails(json);

            Assert.Null(ex.FieldName);
        }

        [Fact]
        public void Parse_TrailingComma_Tolerated()
        {
            // Фактическое поведение сериализатора .NET Framework: висячая запятая прощается, смысл не меняется.
            ConnectionProfile profile = ConnectionProfileFile.Parse(
                "{ \"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\", }");

            Assert.Equal(TimeSpan.FromHours(3), profile.TimeZone.Offset);
        }

        [Fact]
        public void Parse_FieldNamesCaseSensitive()
        {
            // «EwsUrl» вместо «ewsUrl» — поле не найдено; ошибка указывает правильное имя.
            ProfileLoadException ex = ParseFails("{ \"EwsUrl\": \"https://host.example.test/EWS/Exchange.asmx\", \"utcOffset\": \"+03:00\" }");

            Assert.Equal("ewsUrl", ex.FieldName);
        }

        [Fact]
        public void Parse_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ConnectionProfileFile.Parse(null!));
        }

        // ---- Файл ----

        [Fact]
        public void Load_MissingFile_ExplainsHowToCreate()
        {
            ProfileLoadException ex = Assert.Throws<ProfileLoadException>(
                () => ConnectionProfileFile.Load(Path.Combine(_directory, "connection.json")));

            Assert.Contains("connection.example.json", ex.Message);
            Assert.Null(ex.FieldName);
        }

        [Fact]
        public void Load_PathIsDirectory_ReportedAsNotFound()
        {
            Directory.CreateDirectory(_directory);

            Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Load(_directory));
        }

        [Theory]
        [InlineData("utf8bom")]
        [InlineData("utf8")]
        [InlineData("utf16")]
        public void Load_Encodings_Accepted(string encodingName)
        {
            Encoding encoding = encodingName == "utf8bom" ? new UTF8Encoding(true)
                : encodingName == "utf8" ? new UTF8Encoding(false)
                : (Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            string path = WriteFile(ValidJson, encoding);

            Assert.Equal("user@example.test", ConnectionProfileFile.Load(path).MailboxAddress);
        }

        [Fact]
        public void Load_TooLarge_Rejected()
        {
            string path = WriteFile(ValidJson + new string(' ', 70 * 1024), new UTF8Encoding(false));

            ProfileLoadException ex = Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Load(path));

            Assert.Contains("КБ", ex.Message);
        }

        [Fact]
        public void Load_FileLockedForReading_ReportedAsUnreadable()
        {
            string path = WriteFile(ValidJson, new UTF8Encoding(false));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ProfileLoadException ex = Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Load(path));

                Assert.IsAssignableFrom<IOException>(ex.InnerException);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        public void Load_BlankPath_Throws(string path)
        {
            Assert.Throws<ArgumentException>(() => ConnectionProfileFile.Load(path));
        }

        // ---- Запись ----

        [Fact]
        public void Save_ThenLoad_RoundTrip()
        {
            var profile = new ConnectionProfile(
                new Uri("https://host.example.test/EWS/Exchange.asmx"),
                "user@example.test",
                new TimeSpan(-9, -30, 0),
                "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030");
            string path = Path.Combine(_directory, "config", "connection.json");

            ConnectionProfileFile.Save(path, profile);
            ConnectionProfile loaded = ConnectionProfileFile.Load(path);

            Assert.Equal(profile.EwsUrl, loaded.EwsUrl);
            Assert.Equal("user@example.test", loaded.MailboxAddress);
            Assert.Equal(new TimeSpan(-9, -30, 0), loaded.TimeZone.Offset);
            Assert.Equal("A1B2C3D4E5F6071829303A4B5C6D7E8F90102030", loaded.PinnedCertificateThumbprint);
        }

        [Fact]
        public void Save_NullOptionalFields_WrittenAsNull_AndReadable()
        {
            var profile = new ConnectionProfile(new Uri("https://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null);
            string path = Path.Combine(_directory, "connection.json");

            ConnectionProfileFile.Save(path, profile);
            string text = File.ReadAllText(path, Encoding.UTF8);

            Assert.Contains("\"mailboxAddress\": null", text);
            Assert.Contains("\"utcOffset\": \"+00:00\"", text);
            Assert.Contains("\"ewsUrl\": \"https://host.example.test/EWS/Exchange.asmx\"", text);
            Assert.Null(ConnectionProfileFile.Load(path).MailboxAddress);
        }

        [Fact]
        public void Save_OverwritesExisting_NoTempLeft()
        {
            string path = WriteFile(ValidJson, new UTF8Encoding(false));
            var profile = new ConnectionProfile(new Uri("https://other.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null);

            ConnectionProfileFile.Save(path, profile);

            Assert.Equal("other.example.test", ConnectionProfileFile.Load(path).EwsUrl.Host);
            Assert.Single(Directory.GetFiles(_directory));
        }

        [Fact]
        public void ToJson_EscapesSpecialCharacters_StillValidJson()
        {
            // Кавычки и обратная косая в адресе (допустимы в части запроса) не ломают файл.
            var profile = new ConnectionProfile(
                new Uri("https://host.example.test/EWS/Exchange.asmx?q=\"a\\b\""), null, TimeSpan.Zero, null);

            ConnectionProfile parsed = ConnectionProfileFile.Parse(ConnectionProfileFile.ToJson(profile));

            Assert.Equal(profile.EwsUrl.OriginalString, parsed.EwsUrl.OriginalString);
        }

        [Fact]
        public void Save_BlankPath_Throws()
        {
            var profile = new ConnectionProfile(new Uri("https://host.example.test/EWS/Exchange.asmx"), null, TimeSpan.Zero, null);

            Assert.Throws<ArgumentException>(() => ConnectionProfileFile.Save(" ", profile));
        }

        // ---- Контракт с поставляемым шаблоном config\connection.example.json ----

        [Fact]
        public void Template_ContainsEveryField()
        {
            string template = File.ReadAllText(TemplatePath, Encoding.UTF8);

            foreach (string field in new[]
            {
                ConnectionProfileFile.EwsUrlField,
                ConnectionProfileFile.MailboxAddressField,
                ConnectionProfileFile.UtcOffsetField,
                ConnectionProfileFile.PinnedCertificateThumbprintField,
                ConnectionProfileFile.AlternateEwsUrlField,
                ConnectionProfileFile.AlternatePinnedCertificateThumbprintField,
            })
            {
                Assert.Contains("\"" + field + "\":", template);
            }
        }

        [Fact]
        public void Template_AsShipped_RejectedWithPlaceholderHint()
        {
            // Незаполненный шаблон не должен загрузиться молча — пользователь получает подсказку про <host>.
            ProfileLoadException ex = Assert.Throws<ProfileLoadException>(() => ConnectionProfileFile.Load(TemplatePath));

            Assert.Equal("ewsUrl", ex.FieldName);
        }

        [Fact]
        public void Template_WithHostFilledIn_Loads()
        {
            string filled = File.ReadAllText(TemplatePath, Encoding.UTF8).Replace("<host>", "host.example.test");

            ConnectionProfile profile = ConnectionProfileFile.Parse(filled);

            Assert.Equal("host.example.test", profile.EwsUrl.Host);
            Assert.Null(profile.MailboxAddress);
            Assert.Equal(TimeSpan.Zero, profile.TimeZone.Offset);
            Assert.Null(profile.PinnedCertificateThumbprint);
        }
    }
}
