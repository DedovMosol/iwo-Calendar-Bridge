using IwoBridge.Outlook;
using Xunit;

namespace IwoBridge.Outlook.Tests
{
    /// <summary>cid вложения для EWS 2007 (ContentId, без IsInline).</summary>
    public sealed class OutlookAttachmentTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("<>")]
        public void NormalizeContentId_Empty_Null(object? value)
        {
            Assert.Null(OutlookSource.NormalizeContentId(value));
        }

        [Fact]
        public void OleAndFiles_AreCopied_LinksAreNot()
        {
            Assert.True(OutlookSource.IsCopied(1));
            Assert.True(OutlookSource.IsCopied(5));
            Assert.True(OutlookSource.IsCopied(6));
            Assert.False(OutlookSource.IsCopied(4));
            Assert.Equal("рис.bin", OutlookSource.AttachmentName("рис", 6));
        }

        [Fact]
        public void NormalizeContentId_StripsBrackets()
        {
            Assert.Equal("img001.png", OutlookSource.NormalizeContentId("<img001.png>"));
            Assert.Equal("img001.png", OutlookSource.NormalizeContentId("img001.png"));
        }
    }
}
