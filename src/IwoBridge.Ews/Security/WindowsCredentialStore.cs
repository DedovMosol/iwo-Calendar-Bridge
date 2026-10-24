using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using IwoBridge.Core.Security;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace IwoBridge.Ews.Security
{
    /// <summary>
    /// Учётные данные в диспетчере учётных данных Windows (generic credentials). Пароль хранит Windows (DPAPI,
    /// только для текущего пользователя этой машины); приложение его нигде не сохраняет. Записать можно из окна
    /// настроек (<see cref="Save"/>) или штатной командой <c>cmdkey /generic:&lt;target&gt; /user:... /pass</c>;
    /// посмотреть и удалить — «Панель управления → Диспетчер учётных данных → Учётные данные Windows».
    ///
    /// Отсутствие записи (ERROR_NOT_FOUND) — штатный <c>null</c>; любая другая ошибка Windows (например, нет
    /// сеанса входа) — <see cref="Win32Exception"/>, чтобы не выдавать её за «учётные данные не заданы».
    /// </summary>
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        private const int CredTypeGeneric = 1;
        private const int ErrorNotFound = 1168;

        // CRED_PERSIST_LOCAL_MACHINE: запись переживает выход из системы, но не уезжает с перемещаемым профилем.
        private const int CredPersistLocalMachine = 2;

        // CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512 байт; пароль хранится в UTF-16.
        private const int MaxPasswordChars = 5 * 512 / 2;

        // CRED_MAX_USERNAME_LENGTH (wincred.h).
        private const int MaxUserNameChars = 513;

        public CredentialSecret? TryRead(string target)
        {
            RequireTarget(target);

            if (!CredReadW(target, CredTypeGeneric, 0, out IntPtr handle))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }

                throw new Win32Exception(error, "Не удалось прочитать учётные данные из диспетчера учётных данных Windows.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(handle);
                string userName = credential.UserName ?? string.Empty;
                if (userName.Length == 0)
                {
                    return null;
                }

                string password = string.Empty;
                if (credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero)
                {
                    // Пароль хранится в UTF-16; размер задан в байтах.
                    password = Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2) ?? string.Empty;
                }

                return new CredentialSecret(userName, password);
            }
            finally
            {
                CredFree(handle);
            }
        }

        public void Save(string target, CredentialSecret secret)
        {
            RequireTarget(target);
            if (secret is null)
            {
                throw new ArgumentNullException(nameof(secret));
            }

            if (secret.UserName.Length > MaxUserNameChars)
            {
                throw new ArgumentException(
                    $"Имя пользователя длиннее {MaxUserNameChars} символов не помещается в диспетчер учётных данных.", nameof(secret));
            }

            if (secret.Password.Length > MaxPasswordChars)
            {
                throw new ArgumentException(
                    $"Пароль длиннее {MaxPasswordChars} символов не помещается в диспетчер учётных данных.", nameof(secret));
            }

            // Пароль копируется в неуправляемую память только на время вызова и затирается сразу после.
            IntPtr blob = Marshal.StringToCoTaskMemUni(secret.Password);
            try
            {
                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = secret.Password.Length * 2,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    UserName = secret.UserName,
                };

                if (!CredWriteW(ref credential, 0))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), "Не удалось сохранить учётные данные в диспетчер учётных данных Windows.");
                }
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        }

        private static void RequireTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("Имя цели обязательно.", nameof(target));
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW([In] ref NativeCredential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr credential);

        // Раскладка CREDENTIALW (wincred.h); LastWritten — FILETIME, как в заголовке.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }
    }
}
