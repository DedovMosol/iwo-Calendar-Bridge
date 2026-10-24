using System;
using IwoBridge.Core.Security;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// Учётные данные для NTLM: разбор «ДОМЕН\пользователь» на домен и имя, UPN и простое имя не делятся.
    /// Данные синтетические.
    /// </summary>
    public sealed class CredentialSecretTests
    {
        [Fact]
        public void DomainForm_SplitsDomainAndUser()
        {
            var secret = new CredentialSecret("EXAMPLE\\user", "secret");

            Assert.Equal("EXAMPLE", secret.Domain);
            Assert.Equal("user", secret.User);
            Assert.Equal("EXAMPLE\\user", secret.UserName);
        }

        [Fact]
        public void Upn_KeptWholeAsUser()
        {
            var secret = new CredentialSecret("user@example.test", "secret");

            Assert.Null(secret.Domain);
            Assert.Equal("user@example.test", secret.User);
        }

        [Fact]
        public void PlainName_HasNoDomain()
        {
            var secret = new CredentialSecret("user", "secret");

            Assert.Null(secret.Domain);
            Assert.Equal("user", secret.User);
        }

        [Theory]
        [InlineData("EXAMPLE\\")]
        [InlineData("\\user")]
        [InlineData("A\\B\\user")]
        [InlineData(" \\ user")]
        public void MalformedDomainForm_Rejected(string userName)
        {
            // Опечатка не уходит на сервер: NTLM с ней не пройдёт, а неудачный вход увеличивает счётчик блокировки.
            Assert.Throws<ArgumentException>(() => new CredentialSecret(userName, "secret"));
        }

        [Fact]
        public void SpacesAroundParts_Trimmed()
        {
            var secret = new CredentialSecret(" EXAMPLE \\ user ", "secret");

            Assert.Equal("EXAMPLE", secret.Domain);
            Assert.Equal("user", secret.User);
            Assert.Equal("EXAMPLE\\user", secret.UserName);
        }

        [Fact]
        public void BlankUserName_Throws()
        {
            Assert.Throws<ArgumentException>(() => new CredentialSecret("  ", "secret"));
        }

        [Fact]
        public void NullPassword_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CredentialSecret("user", null!));
        }
    }
}
