using System;
using System.Text;

namespace IwoBridge.Core.Sync
{
    /// <summary>
    /// Описание календаря: по [MS-OXCMAIL] встреча — RTF или обычный текст, не HTML как «лучшее» тело.
    /// HTML из Outlook сводим к тексту для Body, оформление — в PR_RTF_COMPRESSED.
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

        /// <summary>Текст для item:Body календаря: теги убраны, переносы из br/p/div сохранены.</summary>
        public static string ToPlainText(string? text)
        {
            if (string.IsNullOrEmpty(text) || !LooksLikeHtml(text))
            {
                return text ?? string.Empty;
            }

            var sb = new StringBuilder(text!.Length);
            bool inTag = false;
            var tag = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!inTag)
                {
                    if (c == '<')
                    {
                        inTag = true;
                        tag.Clear();
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    continue;
                }

                if (c == '>')
                {
                    inTag = false;
                    string name = tag.ToString();
                    int space = name.IndexOf(' ');
                    if (space >= 0)
                    {
                        name = name.Substring(0, space);
                    }

                    name = name.TrimStart('/').ToLowerInvariant();
                    if (name == "br" || name == "p" || name == "div" || name == "tr" || name == "li")
                    {
                        sb.Append('\n');
                    }
                }
                else
                {
                    tag.Append(c);
                }
            }

            return Unescape(sb.ToString()).Trim();
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('&') < 0)
            {
                return value;
            }

            return value
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", ((char)34).ToString())
                .Replace("&apos;", "'");
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
