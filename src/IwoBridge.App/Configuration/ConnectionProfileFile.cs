using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Text;
using System.Xml;
using IwoBridge.Core.Configuration;
using IwoBridge.Core.Time;

namespace IwoBridge.App.Configuration
{
    /// <summary>
    /// Чтение и запись профиля подключения в файле настроек <c>config\connection.json</c>. Окно настроек
    /// заполняет файл само (<see cref="Save"/>); вручную — по шаблону, как описано ниже. Данные установки (адрес
    /// сервера, ящик, пояс, отпечаток) живут только в этом файле и в диспетчере учётных данных Windows, в коде их
    /// нет (T36).
    ///
    /// Как настроить программу:
    ///  1. Скопировать шаблон <c>config\connection.example.json</c> в <c>config\connection.json</c>.
    ///  2. Заполнить поля:
    ///     - <c>ewsUrl</c> — обязательно: адрес EWS, только HTTPS, вида <c>https://&lt;сервер&gt;/EWS/Exchange.asmx</c>;
    ///     - <c>mailboxAddress</c> — SMTP-адрес целевого ящика; <c>null</c> — ящик учётной записи, под которой вход;
    ///     - <c>utcOffset</c> — обязательно: смещение пояса приёмника от UTC вида <c>+ЧЧ:ММ</c> или <c>-ЧЧ:ММ</c>;
    ///     - <c>pinnedCertificateThumbprint</c> — отпечаток сертификата сервера; <c>null</c> — будет взят и показан
    ///       на подтверждение при первом подключении;
    ///     - <c>alternateEwsUrl</c> — необязательно: резервный адрес того же сервера (например, внешний), на который
    ///       программа переходит, если основной недоступен по сети; <c>null</c> — резерва нет;
    ///     - <c>alternatePinnedCertificateThumbprint</c> — отпечаток сертификата резервного адреса (свой).
    ///  3. Пароль в файл не пишется — его хранит диспетчер учётных данных Windows (команда <c>cmdkey</c>, см. README).
    ///
    /// Смещение пояса обязательно и не подставляется по умолчанию: от него считаются границы периода очистки, и
    /// ошибка на несколько часов сдвинула бы период — удалились бы события за его пределами.
    ///
    /// Формат — JSON-объект без комментариев; имена полей чувствительны к регистру; значения — строки или null.
    /// Поля, которых загрузчик не знает, игнорируются — поэтому в шаблоне есть пояснения <c>_comment</c> и
    /// <c>_fields</c>. Сериализатор .NET Framework прощает висячую запятую после последнего поля; на смысл это не
    /// влияет и закреплено тестом.
    ///
    /// Ошибки — <see cref="ProfileLoadException"/> с понятным текстом и именем поля, которое надо исправить.
    /// Значения полей в текст ошибки не попадают никогда: сообщение может уйти в журнал, а данных установки там
    /// быть не должно (T36).
    ///
    /// Разбор — штатным <see cref="DataContractJsonSerializer"/> из .NET Framework (без сторонних пакетов);
    /// проверка значений — в <see cref="ConnectionProfile"/>, единственном месте правил профиля (DRY).
    /// </summary>
    public static class ConnectionProfileFile
    {
        /// <summary>Поле адреса EWS. Совпадает с параметром конструктора <see cref="ConnectionProfile"/>.</summary>
        public const string EwsUrlField = "ewsUrl";

        /// <summary>Поле SMTP-адреса целевого ящика.</summary>
        public const string MailboxAddressField = "mailboxAddress";

        /// <summary>Поле смещения пояса приёмника от UTC.</summary>
        public const string UtcOffsetField = "utcOffset";

        /// <summary>Поле отпечатка закреплённого сертификата.</summary>
        public const string PinnedCertificateThumbprintField = "pinnedCertificateThumbprint";

        /// <summary>Поле необязательного резервного адреса EWS.</summary>
        public const string AlternateEwsUrlField = "alternateEwsUrl";

        /// <summary>Поле отпечатка сертификата резервного адреса.</summary>
        public const string AlternatePinnedCertificateThumbprintField = "alternatePinnedCertificateThumbprint";

        // Настоящий файл занимает около килобайта. Предел защищает от случайно выбранного огромного файла:
        // читать его целиком в память незачем.
        private const long MaxFileBytes = 64 * 1024;

        private const string ExpectedUrlShape = "https://<сервер>/EWS/Exchange.asmx";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Читает и проверяет файл настроек. Путь лучше передавать абсолютный (от папки программы).</summary>
        /// <exception cref="ProfileLoadException">Файла нет, он не читается или содержит ошибку.</exception>
        public static ConnectionProfile Load(string path)
        {
            if (path is null || path.Trim().Length == 0)
            {
                throw new ArgumentException("Нужен путь к файлу настроек.", nameof(path));
            }

            string fullPath = path;
            string json;
            try
            {
                fullPath = Path.GetFullPath(path);
                var file = new FileInfo(fullPath);
                if (!file.Exists)
                {
                    throw new ProfileLoadException(
                        "Файл настроек не найден: " + fullPath + ". Скопируйте шаблон config\\connection.example.json "
                        + "в config\\connection.json и заполните его.");
                }

                if (file.Length > MaxFileBytes)
                {
                    throw new ProfileLoadException(
                        "Файл настроек больше " + (MaxFileBytes / 1024) + " КБ — похоже, выбран не тот файл: " + fullPath);
                }

                // ReadAllText распознаёт BOM (UTF-8, UTF-16, UTF-32) и убирает его; без BOM текст читается как UTF-8.
                json = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                throw Unreadable(fullPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Unreadable(fullPath, ex);
            }
            catch (SecurityException ex)
            {
                throw Unreadable(fullPath, ex);
            }
            catch (NotSupportedException ex)
            {
                throw Unreadable(fullPath, ex);
            }
            catch (ArgumentException ex)
            {
                // Недопустимые символы в пути (Path.GetFullPath, FileInfo).
                throw Unreadable(fullPath, ex);
            }

            return Parse(json);
        }

        /// <summary>Разбирает и проверяет текст файла настроек (используется <see cref="Load"/> и тестами).</summary>
        /// <exception cref="ProfileLoadException">Текст не JSON или поле заполнено неверно.</exception>
        public static ConnectionProfile Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            ProfileDocument document = Deserialize(json);
            Uri ewsUrl = ParseEwsUrl(document.EwsUrl, EwsUrlField)!;
            Uri? alternateEwsUrl = ParseEwsUrl(document.AlternateEwsUrl, AlternateEwsUrlField, required: false);
            TimeSpan utcOffset = ParseUtcOffset(document.UtcOffset);

            try
            {
                return new ConnectionProfile(
                    ewsUrl,
                    document.MailboxAddress,
                    utcOffset,
                    document.PinnedCertificateThumbprint,
                    alternateEwsUrl,
                    document.AlternatePinnedCertificateThumbprint);
            }
            catch (ArgumentException ex)
            {
                // Имена параметров конструктора совпадают с полями файла (контрактный тест), поэтому ParamName
                // указывает поле. В .NET Framework текст исключения дополняется строками «Имя параметра…» и
                // «Фактическое значение…» — берётся только первая строка, чтобы значение не попало в сообщение.
                throw FieldError(ex.ParamName, FirstLine(ex.Message), ex);
            }
        }

        private static ProfileDocument Deserialize(string json)
        {
            // Строка могла прийти без декодирования BOM (например, из буфера обмена): U+FEFF перед JSON недопустим.
            string text = json.TrimStart('\uFEFF');
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                throw new ProfileLoadException("Файл настроек пуст. Заполните его по шаблону config\\connection.example.json.");
            }

            // Сериализатор принимает массив в корне как пустой объект, и пользователь получил бы ложное
            // «не задан ewsUrl». Корень обязан быть объектом — проверяется явно.
            if (trimmed[0] != '{')
            {
                throw NotAnObject();
            }

            var serializer = new DataContractJsonSerializer(typeof(ProfileDocument));
            object? result;
            try
            {
                using (var stream = new MemoryStream(Utf8NoBom.GetBytes(text), writable: false))
                {
                    result = serializer.ReadObject(stream);
                }
            }
            catch (SerializationException ex)
            {
                throw NotJson(ex);
            }
            catch (XmlException ex)
            {
                throw NotJson(ex);
            }

            if (!(result is ProfileDocument document))
            {
                throw NotAnObject();
            }

            return document;
        }

        private static ProfileLoadException NotAnObject()
        {
            return new ProfileLoadException(
                "Файл настроек должен содержать объект JSON с полями профиля: { \"ewsUrl\": …, \"utcOffset\": … }.");
        }

        /// <summary>Адрес EWS из поля; необязательное пустое поле — <c>null</c>. Правила адреса — в профиле.</summary>
        private static Uri? ParseEwsUrl(string? value, string field, bool required = true)
        {
            string text = value is null ? string.Empty : value.Trim();
            if (text.Length == 0)
            {
                if (!required)
                {
                    return null;
                }

                throw FieldError(field, "значение не задано. Укажите адрес EWS вида " + ExpectedUrlShape + ".");
            }

            // Угловые скобки в адресе — недозаполненный шаблон «<host>». Отдельное сообщение понятнее, чем общее
            // «адрес не разобран».
            if (text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0)
            {
                throw FieldError(field, "остался шаблон <host> из примера — укажите адрес своего сервера.");
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
            {
                throw FieldError(field, "не является абсолютным адресом. Ожидается вид " + ExpectedUrlShape + ".");
            }

            return uri;
        }

        private static TimeSpan ParseUtcOffset(string? value)
        {
            string text = value is null ? string.Empty : value.Trim();
            if (text.Length == 0)
            {
                throw FieldError(
                    UtcOffsetField, "значение не задано. Укажите смещение пояса приёмника от UTC, например \"+03:00\".");
            }

            if (!TargetTimeZone.TryParseOffset(text, out TimeSpan offset))
            {
                throw FieldError(
                    UtcOffsetField,
                    "должно иметь вид +ЧЧ:ММ или -ЧЧ:ММ (знак, две цифры часов, двоеточие, две цифры минут), "
                    + "например \"+03:00\" или \"-05:30\".");
            }

            // Предел ±14 часов проверяет TargetTimeZone (через конструктор профиля) — единое место правила.
            return offset;
        }

        /// <summary>
        /// Текст файла настроек для профиля: отформатированный JSON с пояснением, без BOM-зависимостей. Пишется
        /// вручную — несколько строковых полей; сериализатор .NET Framework экранировал бы «/» в адресе («https:\/\/»),
        /// и файл стало бы неудобно править руками.
        /// </summary>
        public static string ToJson(ConnectionProfile profile)
        {
            if (profile is null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var builder = new StringBuilder();
            builder.Append("{\r\n");
            Member(builder, "_comment", "Профиль подключения iwo Calendar Bridge. Пароль здесь не хранится — он в диспетчере "
                + "учётных данных Windows. Описание полей — config/connection.example.json.", last: false);
            Member(builder, EwsUrlField, profile.EwsUrl.OriginalString, last: false);
            Member(builder, MailboxAddressField, profile.MailboxAddress, last: false);
            Member(builder, UtcOffsetField, profile.TimeZone.OffsetText, last: false);
            Member(builder, PinnedCertificateThumbprintField, profile.PinnedCertificateThumbprint, last: false);
            Member(builder, AlternateEwsUrlField, profile.AlternateEwsUrl?.OriginalString, last: false);
            Member(builder, AlternatePinnedCertificateThumbprintField, profile.AlternatePinnedCertificateThumbprint, last: true);
            builder.Append("}\r\n");
            return builder.ToString();
        }

        /// <summary>
        /// Сохраняет профиль атомарно: временный файл рядом → сброс на диск → замена. Оборванная запись не портит
        /// прежние настройки. Папка создаётся при необходимости. Пояснения, добавленные в файл вручную, не
        /// сохраняются — файл перезаписывается целиком.
        /// </summary>
        /// <exception cref="IOException">Нет места, файл занят и т. п.</exception>
        /// <exception cref="UnauthorizedAccessException">Нет прав на запись в папку настроек.</exception>
        /// <exception cref="ArgumentException">Путь пуст или содержит недопустимые символы.</exception>
        /// <exception cref="NotSupportedException">Путь недопустимого вида (например, с двоеточием в середине).</exception>
        public static void Save(string path, ConnectionProfile profile)
        {
            if (path is null || path.Trim().Length == 0)
            {
                throw new ArgumentException("Нужен путь к файлу настроек.", nameof(path));
            }

            string json = ToJson(profile);
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                byte[] bytes = Utf8NoBom.GetBytes(json);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                FileSafety.ReplaceWith(tempPath, fullPath);
            }
            finally
            {
                FileSafety.DeleteQuietly(tempPath);
            }
        }

        private static void Member(StringBuilder builder, string name, string? value, bool last)
        {
            builder.Append("  ").Append(JsonString(name)).Append(": ").Append(value is null ? "null" : JsonString(value));
            builder.Append(last ? "\r\n" : ",\r\n");
        }

        /// <summary>Строка JSON (RFC 8259): кавычки, обратная косая и управляющие символы экранируются.</summary>
        private static string JsonString(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static string FirstLine(string message)
        {
            int end = message.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? message : message.Substring(0, end);
        }

        private static ProfileLoadException FieldError(string? field, string problem, Exception? inner = null)
        {
            string name = field ?? "?";
            return new ProfileLoadException("Ошибка в поле «" + name + "» файла настроек: " + problem, field, inner);
        }

        private static ProfileLoadException NotJson(Exception inner)
        {
            // Текст разборщика описывает, что не так (например, неожиданный символ), — он полезен пользователю.
            return new ProfileLoadException(
                "Файл настроек не является корректным JSON (комментарии // и /* */ не допускаются): "
                + FirstLine(inner.Message),
                null,
                inner);
        }

        private static ProfileLoadException Unreadable(string path, Exception inner)
        {
            return new ProfileLoadException(
                "Не удалось прочитать файл настроек " + path + ": " + FirstLine(inner.Message), null, inner);
        }

        /// <summary>
        /// Форма файла для сериализатора. Все поля — строки или null: разбор и проверка значений выполняются
        /// явно, чтобы ошибка указывала на поле, а не на «неверный тип в позиции N».
        /// </summary>
        [DataContract]
        private sealed class ProfileDocument
        {
            [DataMember(Name = EwsUrlField)]
            public string? EwsUrl { get; set; }

            [DataMember(Name = MailboxAddressField)]
            public string? MailboxAddress { get; set; }

            [DataMember(Name = UtcOffsetField)]
            public string? UtcOffset { get; set; }

            [DataMember(Name = PinnedCertificateThumbprintField)]
            public string? PinnedCertificateThumbprint { get; set; }

            [DataMember(Name = AlternateEwsUrlField)]
            public string? AlternateEwsUrl { get; set; }

            [DataMember(Name = AlternatePinnedCertificateThumbprintField)]
            public string? AlternatePinnedCertificateThumbprint { get; set; }
        }
    }
}
