using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Описание события с оформлением в формате свойства MAPI <c>PR_RTF_COMPRESSED</c> (MS-OXRTFCP).
    /// Outlook 2010+ отдаёт сырой документ (<c>RTFBody</c>) — его оборачиваем несжатым «MELA».
    /// Outlook 2007 отдаёт уже сжатое значение <c>0x1009</c> (MELA или LZFu) — его пишем как есть, без второго заголовка.
    /// </summary>
    public static class MapiRtf
    {
        private const int HeaderSize = 16;
        private const uint Uncompressed = 0x414C454D; // «MELA»
        private const uint Lzfu = 0x75465A4C;         // «LZFu»
        private static readonly byte[] PictMarker = Encoding.ASCII.GetBytes("\\pict");
        private static readonly byte[] ObjMarker = Encoding.ASCII.GetBytes("\\objdata");

        // Начальный словарь LZFu, как у POI/MSGReader (реальные MSG/Outlook). «207 байт» в части пересказов спецификации
        // не совпадает с этой строкой — длина сида задаёт write-offset словаря.
        private static readonly byte[] LzfuSeed = Encoding.ASCII.GetBytes(
            "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fcharset0 Times New Roman;}{\\colortbl;\\red0\\green0\\blue0;}\r\n\\pard\\plain\\f0\\fs24\\cf0 ");

        /// <summary>
        /// Уже значение <c>PR_RTF_COMPRESSED</c> (MELA или LZFu). COMPSIZE равен длине минус 4 — иначе байты
        /// внутри <c>{\rtf</c> не примем за заголовок и не отправим документ без MELA.
        /// </summary>
        public static bool IsCompressedValue(byte[]? value)
        {
            if (value == null || value.Length < HeaderSize)
            {
                return false;
            }

            uint type = Read(value, 8);
            if (type != Uncompressed && type != Lzfu)
            {
                return false;
            }

            return Read(value, 0) == (uint)(value.Length - 4);
        }

        /// <summary>
        /// Документ <c>{\rtf</c> — в MELA; уже сжатое значение — как есть (Outlook 2007 отдаёт 0x1009 сжатым).
        /// </summary>
        public static byte[] EnsureCompressed(byte[] value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return IsCompressedValue(value) ? value : FromRtf(value);
        }

        /// <summary>
        /// Переносить ли описание. Пустая RTF-заготовка Outlook без картинки — нет. LZFu распаковывается по MS-OXRTFCP
        /// (Outlook 2007): картинка без текста не теряется, пустышка не копируется.
        /// </summary>
        public static bool ShouldCopy(byte[]? value, string? body)
        {
            if (value == null || value.Length == 0)
            {
                return false;
            }

            if (TryGetDocument(value, out byte[] document))
            {
                return (body ?? string.Empty).Trim().Length > 0 || HasPicture(document);
            }

            // Распаковка не вышла (другой порядок бит у части клиентов) — не теряем оформление, если есть текст.
            return IsCompressedValue(value) && (body ?? string.Empty).Trim().Length > 0;
        }

        /// <summary>Вставлена ли в RTF картинка (\pict) или OLE (\objdata). Поиск по байтам, без строки на весь буфер.</summary>
        public static bool HasPicture(byte[] rtf)
        {
            if (rtf is null)
            {
                return false;
            }

            return Contains(rtf, PictMarker) || Contains(rtf, ObjMarker);
        }

        /// <summary>RTF (байты документа) в значение свойства <c>PR_RTF_COMPRESSED</c> без сжатия.</summary>
        public static byte[] FromRtf(byte[] rtf)
        {
            if (rtf is null)
            {
                throw new ArgumentNullException(nameof(rtf));
            }

            var result = new byte[HeaderSize + rtf.Length];
            Write(result, 0, (uint)(rtf.Length + HeaderSize - 4));
            Write(result, 4, (uint)rtf.Length);
            Write(result, 8, Uncompressed);
            Write(result, 12, 0);
            Buffer.BlockCopy(rtf, 0, result, HeaderSize, rtf.Length);
            return result;
        }

        /// <summary>Похоже ли это на RTF-документ (Outlook отдаёт пустой RTF и для событий без описания).</summary>
        public static bool LooksLikeRtf(byte[]? rtf) =>
            rtf != null && rtf.Length > 5 && rtf[0] == (byte)'{' && rtf[1] == (byte)'\\' && rtf[2] == (byte)'r' && rtf[3] == (byte)'t' && rtf[4] == (byte)'f';

        /// <summary>Достать <c>{\rtf</c> из сырого документа, MELA или LZFu (MS-OXRTFCP).</summary>
        public static bool TryGetDocument(byte[] value, out byte[] document)
        {
            if (LooksLikeRtf(value))
            {
                document = value;
                return true;
            }

            if (value != null && IsCompressedValue(value))
            {
                uint type = Read(value, 8);
                if (type == Uncompressed)
                {
                    var payload = new byte[value.Length - HeaderSize];
                    Buffer.BlockCopy(value, HeaderSize, payload, 0, payload.Length);
                    if (LooksLikeRtf(payload))
                    {
                        document = payload;
                        return true;
                    }
                }
                else if (TryDecompressLzfu(value, out byte[] raw) && LooksLikeRtf(raw))
                {
                    document = raw;
                    return true;
                }
            }

            document = Array.Empty<byte>();
            return false;
        }

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

        /// <summary>Распаковка LZFu по MS-OXRTFCP. CRC не проверяем (у несжатого и части клиентов он 0).</summary>
        private static bool TryDecompressLzfu(byte[] value, out byte[] document)
        {
            document = Array.Empty<byte>();
            uint rawSize = Read(value, 4);
            if (rawSize == 0 || rawSize > 16 * 1024 * 1024)
            {
                return false;
            }

            var dest = new byte[rawSize];
            var dict = new byte[4096];
            int seed = Math.Min(LzfuSeed.Length, dict.Length);
            Buffer.BlockCopy(LzfuSeed, 0, dict, 0, seed);
            int dictOff = seed;
            int inPos = HeaderSize;
            int outPos = 0;
            while (outPos < dest.Length && inPos < value.Length)
            {
                int control = value[inPos++];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (outPos >= dest.Length)
                    {
                        break;
                    }

                    if (inPos >= value.Length)
                    {
                        document = Array.Empty<byte>();
                        return false;
                    }

                    if ((control & (1 << bit)) != 0)
                    {
                        if (inPos + 1 >= value.Length)
                        {
                            return false;
                        }

                        int token = (value[inPos] << 8) | value[inPos + 1];
                        inPos += 2;
                        int offset = token >> 4;
                        int length = (token & 0xF) + 2;
                        for (int n = 0; n < length && outPos < dest.Length; n++)
                        {
                            byte c = dict[(offset + n) & 4095];
                            dest[outPos++] = c;
                            dict[dictOff & 4095] = c;
                            dictOff++;
                        }
                    }
                    else
                    {
                        byte c = value[inPos++];
                        dest[outPos++] = c;
                        dict[dictOff & 4095] = c;
                        dictOff++;
                    }
                }
            }

            if (outPos != dest.Length)
            {
                return false;
            }

            document = dest;
            return true;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            int last = haystack.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                int n = 0;
                while (n < needle.Length && haystack[i + n] == needle[n])
                {
                    n++;
                }

                if (n == needle.Length)
                {
                    return true;
                }
            }

            return false;
        }

        private static uint Read(byte[] source, int offset) =>
            source[offset]
            | ((uint)source[offset + 1] << 8)
            | ((uint)source[offset + 2] << 16)
            | ((uint)source[offset + 3] << 24);

        private static void Write(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }
}
