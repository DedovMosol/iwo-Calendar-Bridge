using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    public sealed class EventBodyTests
    {
        [Fact]
        public void LooksLikeHtml_Tags_NotPlainText()
        {
            Assert.True(EventBody.LooksLikeHtml("<html><body>x</body></html>"));
            Assert.False(EventBody.LooksLikeHtml("plain"));
        }

        [Fact]
        public void ToPlainText_StripsTags_KeepsBreaks()
        {
            Assert.Equal("hello", EventBody.ToPlainText("<html><body>hello</body></html>"));
            Assert.Equal("A & B", EventBody.ToPlainText("<div>A &amp; B</div>"));
        }
    }
}
