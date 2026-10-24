using System;
using IwoBridge.Core.Transport;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Нормализация отпечатка сертификата: разделители и невидимые символы (U+200E из диалога Windows) убираются, допустимы только 40 и 64 шестнадцатеричных символа.
    /// </summary>
    public sealed class ThumbprintTests
    {
        private const string Sha1 = "A1B2C3D4E5F6071829303A4B5C6D7E8F90102030";
        private const string Sha256 = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

        [Fact]
        public void Null_ReturnsNull() => Assert.Null(Thumbprint.Normalize(null));

        [Fact]
        public void Blank_ReturnsNull() => Assert.Null(Thumbprint.Normalize("  \t "));

        [Fact]
        public void StripsSeparatorsAndUppercases()
        {
            Assert.Equal(Sha1, Thumbprint.Normalize("a1:b2:c3:d4:e5:f6:07:18:29:30:3a:4b:5c:6d:7e:8f:90:10:20:30"));
            Assert.Equal(Sha1, Thumbprint.Normalize("a1 b2 c3 d4 e5 f6 07 18 29 30 3a 4b 5c 6d 7e 8f 90 10 20 30"));
            Assert.Equal(Sha1, Thumbprint.Normalize("A1-B2-C3-D4-E5-F6-07-18-29-30-3A-4B-5C-6D-7E-8F-90-10-20-30"));
        }

        [Fact]
        public void StripsInvisibleFormatCharacters()
        {
            // Диалог сертификатов Windows добавляет невидимый U+200E при копировании отпечатка.
            Assert.Equal(Sha1, Thumbprint.Normalize("\u200E" + Sha1.ToLowerInvariant()));
            Assert.Equal(Sha1, Thumbprint.Normalize("\uFEFF" + Sha1 + "\u200F"));
        }

        [Fact]
        public void AcceptsSha256() => Assert.Equal(Sha256, Thumbprint.Normalize(Sha256.ToLowerInvariant()));

        [Theory]
        [InlineData("A1B2C3D4E5F6071829303A4B5C6D7E8F9010203")]
        [InlineData("A1B2C3D4E5F6071829303A4B5C6D7E8F901020301")]
        public void WrongLength_Throws(string value)
        {
            Assert.Throws<FormatException>(() => Thumbprint.Normalize(value));
        }

        [Fact]
        public void NonHexCharacter_Throws()
        {
            Assert.Throws<FormatException>(() => Thumbprint.Normalize("G1B2C3D4E5F6071829303A4B5C6D7E8F90102030"));
        }

        [Fact]
        public void ToHex_UppercaseWithoutSeparators()
        {
            Assert.Equal("0AFF10", Thumbprint.ToHex(new byte[] { 0x0A, 0xFF, 0x10 }));
        }
    }
}
