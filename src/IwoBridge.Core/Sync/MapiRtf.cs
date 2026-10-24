using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Описание события с оформлением в формате свойства MAPI <c>PR_RTF_COMPRESSED</c> (MS-OXRTFCP). Календари Outlook
    /// хранят описание в RTF — вместе со шрифтами, таблицами и вставленными картинками; Exchange принимает это свойство
    /// по EWS как есть и сам строит из него HTML для OWA и телефонов. Поэтому копия получает исходный RTF, а не текст
    /// (проверено вживую: RTF 500 КБ с картинкой записан и прочитан без потерь).
    ///
    /// Outlook отдаёт RTF без сжатия (<c>AppointmentItem.RTFBody</c>); записывается он в несжатом виде формата —
    /// заголовок 16 байт с сигнатурой «MELA», это допустимо по MS-OXRTFCP §2.1.3.1.1.
    /// </summary>
    public static class MapiRtf
    {
        private const int HeaderSize = 16;
        private const uint Uncompressed = 0x414C454D; // «MELA»

        /// <summary>RTF (байты документа) в значение свойства <c>PR_RTF_COMPRESSED</c> без сжатия.</summary>
        public static byte[] FromRtf(byte[] rtf)
        {
            if (rtf is null)
            {
                throw new ArgumentNullException(nameof(rtf));
            }

            var result = new byte[HeaderSize + rtf.Length];
            Write(result, 0, (uint)(rtf.Length + HeaderSize - 4)); // COMPSIZE: всё после этого поля
            Write(result, 4, (uint)rtf.Length);                   // RAWSIZE
            Write(result, 8, Uncompressed);                       // COMPTYPE
            Write(result, 12, 0);                                 // CRC (для несжатого — 0)
            Buffer.BlockCopy(rtf, 0, result, HeaderSize, rtf.Length);
            return result;
        }

        /// <summary>Похоже ли это на RTF-документ (Outlook отдаёт пустой RTF и для событий без описания).</summary>
        public static bool LooksLikeRtf(byte[]? rtf) =>
            rtf != null && rtf.Length > 5 && rtf[0] == (byte)'{' && rtf[1] == (byte)'\\' && rtf[2] == (byte)'r' && rtf[3] == (byte)'t' && rtf[4] == (byte)'f';

        /// <summary>SHA-256 значения — для отпечатка события (смена оформления или картинки — тоже изменение).</summary>
        public static string Fingerprint(byte[] value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(value).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            }
        }

        private static void Write(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }
}
