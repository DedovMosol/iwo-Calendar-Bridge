using System;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Текст описания события: обычный или HTML (OWA, новые Outlook, картинки cid:). Exchange 2007 принимает HTML
    /// в <c>Body</c> и сам строит RTF, пустой текстовый Body при этом не шлём — иначе сервер затирает оформление.
    /// </summary>
    public static class EventBody
    {
        /// <summary>Похоже ли значение на HTML-разметку, а не на обычный текст.</summary>
        public static bool LooksLikeHtml(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string t = text!.TrimStart();
            if (t.Length < 3 || t[0] != '<')
            {
                return false;
            }

            return StartsWithTag(t, "!DOCTYPE")
                || StartsWithTag(t, "html")
                || StartsWithTag(t, "div")
                || StartsWithTag(t, "p")
                || StartsWithTag(t, "span")
                || StartsWithTag(t, "br")
                || StartsWithTag(t, "table")
                || StartsWithTag(t, "font")
                || StartsWithTag(t, "body")
                || StartsWithTag(t, "img");
        }

        private static bool StartsWithTag(string text, string name)
        {
            if (text.Length < name.Length + 1 || text[0] != '<')
            {
                return false;
            }

            return text.Substring(1, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
