using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using IwoBridge.Core.Security;
using IwoBridge.Ews.Security;
using Xunit;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Настоящий диспетчер учётных данных Windows: запись и чтение под уникальной тестовой целью, которая
    /// удаляется после каждого теста. Рабочую цель программы тест не трогает. Данные синтетические.
    ///
    /// Если у процесса нет сеанса входа (ERROR_NO_SUCH_LOGON_SESSION — бывает у служебных учётных записей
    /// сборочных машин), диспетчер недоступен в принципе; тогда тест проверяет лишь, что ошибка сообщается
    /// исключением, а не «учётных данных нет».
    /// </summary>
    public sealed class WindowsCredentialStoreTests : IDisposable
    {
        private const int CredTypeGeneric = 1;
        private const int ErrorNoSuchLogonSession = 1312;

        private readonly string _target = "IwoCalendarBridge/test-" + Guid.NewGuid().ToString("N");
        private readonly WindowsCredentialStore _store = new WindowsCredentialStore();

        public void Dispose()
        {
            CredDeleteW(_target, CredTypeGeneric, 0);
        }

        [Fact]
        public void Delete_RemovesRecord_MissingIsNoOp()
        {
            CredentialSecret? read = TryOrSkip(() =>
            {
                _store.Delete(_target);
                _store.Save(_target, new CredentialSecret("user", "secret"));
                _store.Delete(_target);
                _store.Delete(_target);
                return _store.TryRead(_target);
            });

            Assert.Null(read);
        }

        [Fact]
        public void Missing_ReturnsNull()
        {
            Assert.Null(TryOrSkip(() => _store.TryRead(_target)));
        }

        [Fact]
        public void SaveThenRead_RoundTrip_DomainSplit()
        {
            CredentialSecret? read = TryOrSkip(() =>
            {
                _store.Save(_target, new CredentialSecret("ДОМЕН\\пользователь", "пароль-Ё:;\"\\ 😀"));
                return _store.TryRead(_target);
            });
            if (read is null && _skipped)
            {
                return;
            }

            Assert.NotNull(read);
            Assert.Equal("ДОМЕН\\пользователь", read!.UserName);
            Assert.Equal("ДОМЕН", read.Domain);
            Assert.Equal("пользователь", read.User);
            Assert.Equal("пароль-Ё:;\"\\ 😀", read.Password);
        }

        [Fact]
        public void Save_Twice_Replaces()
        {
            CredentialSecret? read = TryOrSkip(() =>
            {
                _store.Save(_target, new CredentialSecret("user-a", "first"));
                _store.Save(_target, new CredentialSecret("user-b", "second"));
                return _store.TryRead(_target);
            });
            if (read is null && _skipped)
            {
                return;
            }

            Assert.Equal("user-b", read!.UserName);
            Assert.Equal("second", read.Password);
        }

        [Fact]
        public void Save_EmptyPassword_RoundTrip()
        {
            CredentialSecret? read = TryOrSkip(() =>
            {
                _store.Save(_target, new CredentialSecret("user", string.Empty));
                return _store.TryRead(_target);
            });
            if (read is null && _skipped)
            {
                return;
            }

            Assert.Equal(string.Empty, read!.Password);
        }

        [Fact]
        public void Save_TooLongPassword_Rejected_BeforeCallingWindows()
        {
            Assert.Throws<ArgumentException>(() => _store.Save(_target, new CredentialSecret("user", new string('x', 1281))));
        }

        [Fact]
        public void BlankTarget_Rejected()
        {
            Assert.Throws<ArgumentException>(() => _store.TryRead(" "));
            Assert.Throws<ArgumentException>(() => _store.Save(" ", new CredentialSecret("user", "p")));
        }

        private bool _skipped;

        private T? TryOrSkip<T>(Func<T?> action)
            where T : class
        {
            try
            {
                return action();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoSuchLogonSession)
            {
                _skipped = true;
                return null;
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string target, int type, int flags);
    }
}
