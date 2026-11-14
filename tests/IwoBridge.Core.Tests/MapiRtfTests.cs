using System.Text;
using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    /// <summary>
    /// PR_RTF_COMPRESSED: сырой {\rtf оборачивается MELA один раз; значение с заголовком (Outlook 2007, 0x1009) не
    /// оборачивается повторно; пустая заготовка без картинки не копируется.
    /// </summary>
    public sealed class MapiRtfTests
    {
        private static readonly byte[] Document = Encoding.ASCII.GetBytes(@"{\rtf1\ansi\b text\b0\par}");
        private static readonly byte[] WithPicture = Encoding.ASCII.GetBytes(@"{\rtf1\ansi{\pict\wmetafile0 }}");

        [Fact]
        public void FromRtf_IsCompressedValue_RoundTripDocument()
        {
            byte[] compressed = MapiRtf.FromRtf(Document);

            Assert.True(MapiRtf.IsCompressedValue(compressed));
            Assert.True(MapiRtf.TryGetDocument(compressed, out byte[] back));
            Assert.Equal(Document, back);
        }

        [Fact]
        public void EnsureCompressed_DoesNotWrapTwice()
        {
            byte[] once = MapiRtf.FromRtf(Document);
            byte[] twice = MapiRtf.EnsureCompressed(once);

            Assert.Same(once, twice);
        }

        [Fact]
        public void EnsureCompressed_WrapsBareRtf()
        {
            byte[] compressed = MapiRtf.EnsureCompressed(Document);

            Assert.True(MapiRtf.IsCompressedValue(compressed));
            Assert.NotEqual(Document.Length, compressed.Length);
        }

        [Fact]
        public void ShouldCopy_EmptyStubWithoutPicture_False()
        {
            byte[] stub = Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Arial;}}\f0\fs20\par}");

            Assert.False(MapiRtf.ShouldCopy(stub, "   "));
            Assert.True(MapiRtf.ShouldCopy(stub, "текст"));
            Assert.True(MapiRtf.ShouldCopy(WithPicture, ""));
        }

        [Fact]
        public void HasPicture_PictAndObjdata()
        {
            Assert.True(MapiRtf.HasPicture(WithPicture));
            Assert.False(MapiRtf.HasPicture(Document));
        }

        [Fact]
        public void BareRtf_IsNotCompressedValue()
        {
            Assert.False(MapiRtf.IsCompressedValue(Document));
        }

        [Fact]
        public void Lzfu_LiteralStream_RoundTrip_AndPictWithoutBody()
        {
            byte[] lzfu = LzfuLiterals(Document);
            Assert.True(MapiRtf.IsCompressedValue(lzfu));
            Assert.True(MapiRtf.TryGetDocument(lzfu, out byte[] back));
            Assert.Equal(Document, back);
            Assert.True(MapiRtf.ShouldCopy(lzfu, "текст"));
            Assert.False(MapiRtf.ShouldCopy(lzfu, ""));
            Assert.Same(lzfu, MapiRtf.EnsureCompressed(lzfu));

            byte[] pictLzfu = LzfuLiterals(WithPicture);
            Assert.True(MapiRtf.ShouldCopy(pictLzfu, ""));
        }

        /// <summary>LZFu из одних литералов (все биты управляющего байта 0) — проверяет распаковку без ссылок на словарь.</summary>
        private static byte[] LzfuLiterals(byte[] raw)
        {
            var payload = new System.Collections.Generic.List<byte>();
            int i = 0;
            while (i < raw.Length)
            {
                int n = System.Math.Min(8, raw.Length - i);
                payload.Add(0);
                for (int k = 0; k < n; k++)
                {
                    payload.Add(raw[i++]);
                }
            }

            var result = new byte[16 + payload.Count];
            uint compSize = (uint)(result.Length - 4);
            result[0] = (byte)compSize;
            result[1] = (byte)(compSize >> 8);
            result[2] = (byte)(compSize >> 16);
            result[3] = (byte)(compSize >> 24);
            uint rawSize = (uint)raw.Length;
            result[4] = (byte)rawSize;
            result[5] = (byte)(rawSize >> 8);
            result[6] = (byte)(rawSize >> 16);
            result[7] = (byte)(rawSize >> 24);
            uint lzfu = 0x75465A4C;
            result[8] = (byte)lzfu;
            result[9] = (byte)(lzfu >> 8);
            result[10] = (byte)(lzfu >> 16);
            result[11] = (byte)(lzfu >> 24);
            payload.CopyTo(result, 16);
            return result;
        }
    }
}
