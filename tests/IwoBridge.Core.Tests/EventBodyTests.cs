using IwoBridge.Core.Sync;
using Xunit;

namespace IwoBridge.Core.Tests
{
    public sealed class EventBodyTests
    {
        [Fact]
        public void LooksLikeHtml_Tags_NotPlainText()
        {
            Assert.True(EventBody.LooksLikeHtml("<html><body>Привет</body></html>"));
            Assert.True(EventBody.LooksLikeHtml("  <div>x</div>"));
            Assert.True(EventBody.LooksLikeHtml("<p>абзац</p>"));
            Assert.True(EventBody.LooksLikeHtml("<img src=\"cid:x\">"));
            Assert.False(EventBody.LooksLikeHtml("просто текст"));
            Assert.False(EventBody.LooksLikeHtml("<3"));
            Assert.False(EventBody.LooksLikeHtml(""));
            Assert.False(EventBody.LooksLikeHtml(null));
        }
    }
}
