using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IwoBridge.Core.Time;

namespace IwoBridge.Core.Sync
{
    /// <summary>Вложение события — для отпечатка содержимого: имя, размер и SHA-256 содержимого.</summary>
    public sealed class SyncAttachment
    {
        public SyncAttachment(string name, long length, string contentSha256)
        {
            Name = name ?? string.Empty;
            Length = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));
            ContentSha256 = (contentSha256 ?? throw new ArgumentNullException(nameof(contentSha256))).ToUpperInvariant();
        }

        public string Name { get; }

        public long Length { get; }

        public string ContentSha256 { get; }
    }

    /// <summary>
    /// То, что переносится в копию и потому решает, «изменилось ли событие». Заполняет адаптер источника (Outlook);
    /// отпечаток <see cref="Hash"/> записывается в копию (свойство <c>IwoBridgeContentHash</c>) и при следующей
    /// синхронизации сравнивается с отпечатком источника — так изменение видно без чтения содержимого копии.
    ///
    /// Чего здесь нет намеренно: служебных полей Outlook (время изменения, ChangeKey, счётчики) — они меняются без
    /// изменения смысла и вызывали бы лишние перезаписи; участников и ответов — встречи копируются как простые
    /// события (без приглашений).
    /// </summary>
    public sealed class SyncContent
    {
        /// <summary>Версия правила отпечатка. Меняется — все копии один раз перезаписываются (это ожидаемо).</summary>
        public const string HashVersion = "v1";

        public SyncContent(
            string? subject,
            string? location,
            string? body,
            DateTime startUtc,
            DateTime endUtc,
            bool isAllDay,
            string? busyStatus = null,
            string? recurrence = null,
            IEnumerable<string>? exceptions = null,
            IEnumerable<SyncAttachment>? attachments = null,
            string? sensitivity = null)
        {
            Subject = subject ?? string.Empty;
            Location = location ?? string.Empty;
            Body = NormalizeBody(body);
            StartUtc = TimeMapper.ToUtc(startUtc);
            EndUtc = TimeMapper.ToUtc(endUtc);
            IsAllDay = isAllDay;
            BusyStatus = busyStatus ?? string.Empty;
            Sensitivity = sensitivity ?? string.Empty;
            Recurrence = recurrence ?? string.Empty;

            // Порядок исключений и вложений в Outlook не гарантирован — для отпечатка он не важен.
            Exceptions = (exceptions ?? Array.Empty<string>()).Select(e => e ?? string.Empty).OrderBy(e => e, StringComparer.Ordinal).ToArray();
            Attachments = (attachments ?? Array.Empty<SyncAttachment>())
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ThenBy(a => a.ContentSha256, StringComparer.Ordinal)
                .ToArray();
        }

        public string Subject { get; }

        public string Location { get; }

        /// <summary>Текст описания: переводы строк приведены к \n, пробелы в концах строк и текста убраны.</summary>
        public string Body { get; }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        public bool IsAllDay { get; }

        /// <summary>Занятость («занят», «под вопросом»…) — строкой адаптера.</summary>
        public string BusyStatus { get; }

        /// <summary>
        /// Конфиденциальность («обычное», «личное»…) — строкой адаптера: от неё зависит, что переносится в копию
        /// (PLAN.md §6.3), поэтому её смена — тоже изменение. Напоминаний здесь нет: на копиях они выключены.
        /// </summary>
        public string Sensitivity { get; }

        /// <summary>Правило повторения в каноническом виде адаптера; пусто — одиночное событие.</summary>
        public string Recurrence { get; }

        /// <summary>Исключения серии (изменённые и удалённые повторения) в каноническом виде адаптера.</summary>
        public IReadOnlyList<string> Exceptions { get; }

        public IReadOnlyList<SyncAttachment> Attachments { get; }

        /// <summary>
        /// Отпечаток: «v1:» + SHA-256 (шестнадцатеричный) канонической записи полей. Каждое поле записывается с
        /// длиной впереди — разные наборы полей не могут дать одну запись («ab»+«c» ≠ «a»+«bc»).
        /// </summary>
        public string Hash
        {
            get
            {
                var text = new StringBuilder();
                Field(text, Subject);
                Field(text, Location);
                Field(text, Body);
                Field(text, StartUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                Field(text, EndUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                Field(text, IsAllDay ? "1" : "0");
                Field(text, BusyStatus);
                Field(text, Sensitivity);
                Field(text, Recurrence);
                Field(text, Exceptions.Count.ToString(CultureInfo.InvariantCulture));
                foreach (string exception in Exceptions)
                {
                    Field(text, exception);
                }

                Field(text, Attachments.Count.ToString(CultureInfo.InvariantCulture));
                foreach (SyncAttachment attachment in Attachments)
                {
                    Field(text, attachment.Name);
                    Field(text, attachment.Length.ToString(CultureInfo.InvariantCulture));
                    Field(text, attachment.ContentSha256);
                }

                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                    return HashVersion + ":" + string.Concat(digest.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                }
            }
        }

        private static void Field(StringBuilder text, string value)
        {
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }

        private static string NormalizeBody(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            string[] lines = body!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return string.Join("\n", lines.Select(l => l.TrimEnd())).TrimEnd();
        }
    }
}
