using System;
using System.Globalization;

namespace IwoBridge.Core.Configuration
{
    /// <summary>
    /// Адрес сервера, как его вводит человек, → адрес EWS, и обратно. Одно поле «Сервер» принимает любой вариант —
    /// внутренний (в сети организации) или внешний (из интернета):
    /// <list type="bullet">
    ///   <item>имя сервера: <c>mail.example.test</c> → <c>https://mail.example.test/EWS/Exchange.asmx</c>;</item>
    ///   <item>IP-адрес: <c>10.0.0.5</c>, IPv6 — в квадратных скобках: <c>[fd00::5]</c>;</item>
    ///   <item>с портом: <c>mail.example.test:8443</c>;</item>
    ///   <item>полный адрес EWS, например скопированный из Outlook: <c>https://mail.example.test/EWS/Exchange.asmx</c>;</item>
    ///   <item>адрес до папки EWS: <c>https://mail.example.test/ews</c> — имя службы дописывается.</item>
    /// </list>
    ///
    /// Отклоняется всё, что отправило бы пароль не туда или открытым текстом (<see cref="Validate"/> — единое правило и
    /// для поля, и для файла настроек):
    /// <list type="bullet">
    ///   <item><c>http://</c> — пароль ушёл бы без шифрования;</item>
    ///   <item>имя и пароль в адресе (<c>user:pass@host</c>) — пароль оказался бы в файле настроек, а запись
    ///     <c>mail.example.test@evil.test</c> на деле ведёт на evil.test;</item>
    ///   <item>старые записи IPv4 (<c>010.0.0.5</c> — восьмеричная, это 8.0.0.5; <c>10.5</c> — это 10.0.0.5): адрес
    ///     был бы не тем, что набран;</item>
    ///   <item>пробелы и невидимые символы, порт 0, фрагмент <c>#…</c>.</item>
    /// </list>
    /// Чистая функция без сети: доступность адреса проверяет «Проверить подключение».
    /// </summary>
    public static class ServerAddress
    {
        /// <summary>Стандартный путь службы EWS на сервере Exchange.</summary>
        public const string StandardEwsPath = "/EWS/Exchange.asmx";

        private const string EwsFolder = "/EWS";

        private const string NotAnAddress =
            "Не похоже на адрес сервера. Пример: mail.example.ru, 10.0.0.5, [fd00::5] или https://mail.example.ru/EWS/Exchange.asmx.";

        /// <summary>
        /// Разбирает ввод. При ошибке возвращает false и понятное описание (без повторения введённого значения).
        /// </summary>
        public static bool TryParse(string? input, out Uri? ewsUrl, out string? error)
        {
            ewsUrl = null;
            error = null;
            string text = input is null ? string.Empty : input.Trim();
            if (text.Length == 0)
            {
                error = "Укажите сервер: имя, IP-адрес или полный адрес EWS.";
                return false;
            }

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                error = "Нужен защищённый адрес https:// — по http пароль ушёл бы в сеть открытым текстом.";
                return false;
            }

            if (ContainsInvisibleOrSpace(text))
            {
                error = "В адресе есть пробел или невидимый символ — наберите адрес заново.";
                return false;
            }

            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                // «https:/host» — опечатка в схеме, а не имя сервера «https».
                if (text.IndexOf(":/", StringComparison.Ordinal) >= 0)
                {
                    error = NotAnAddress;
                    return false;
                }

                text = "https://" + text;
                schemeEnd = "https".Length;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
            {
                error = NotAnAddress;
                return false;
            }

            error = Validate(uri);
            if (error is null && uri.HostNameType == UriHostNameType.IPv4 && !TypedHostMatches(text, schemeEnd, uri))
            {
                error = "Такая запись IP-адреса понимается по-разному. Введите адрес из четырёх чисел без ведущих нулей, например 10.0.0.5.";
            }

            if (error != null)
            {
                return false;
            }

            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length == 0 || string.Equals(path, EwsFolder, StringComparison.OrdinalIgnoreCase))
            {
                path = StandardEwsPath;
            }

            ewsUrl = new UriBuilder(uri) { Path = path }.Uri;
            return true;
        }

        /// <summary>
        /// Единое правило допустимого адреса EWS (для поля «Сервер» и файла настроек): абсолютный https, есть имя
        /// сервера, нет имени и пароля в адресе, порт не 0, нет фрагмента. Возвращает описание проблемы или <c>null</c>.
        /// </summary>
        public static string? Validate(Uri? ewsUrl)
        {
            if (ewsUrl is null || !ewsUrl.IsAbsoluteUri || ewsUrl.Host.Length == 0)
            {
                return NotAnAddress;
            }

            if (!string.Equals(ewsUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return "Нужен защищённый адрес https:// — по http пароль ушёл бы в сеть открытым текстом.";
            }

            if (ewsUrl.UserInfo.Length > 0)
            {
                return "Имя и пароль в адресе не указываются — для них есть отдельные поля.";
            }

            if (ewsUrl.Port <= 0)
            {
                return "Порт должен быть числом от 1 до 65535.";
            }

            if (ewsUrl.Fragment.Length > 0)
            {
                return NotAnAddress;
            }

            return null;
        }

        /// <summary>
        /// Короткая запись для поля «Сервер»: для стандартного пути — только сервер (и порт, если не 443), иначе
        /// полный адрес. Разбор короткой записи возвращает тот же адрес EWS.
        /// </summary>
        public static string ToDisplay(Uri ewsUrl)
        {
            if (ewsUrl is null)
            {
                throw new ArgumentNullException(nameof(ewsUrl));
            }

            bool standardPath = string.Equals(ewsUrl.AbsolutePath, StandardEwsPath, StringComparison.OrdinalIgnoreCase)
                && ewsUrl.Query.Length == 0;
            if (!standardPath)
            {
                return ewsUrl.OriginalString;
            }

            return ewsUrl.IsDefaultPort ? ewsUrl.Host : ewsUrl.Host + ":" + ewsUrl.Port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Набранный IPv4-адрес совпадает с тем, как его понял разборщик (нет восьмеричных и сокращённых записей).</summary>
        private static bool TypedHostMatches(string text, int schemeEnd, Uri uri)
        {
            int hostStart = schemeEnd + "://".Length;
            int hostEnd = text.IndexOfAny(new[] { ':', '/', '?', '#' }, hostStart);
            string typed = hostEnd < 0 ? text.Substring(hostStart) : text.Substring(hostStart, hostEnd - hostStart);
            return string.Equals(typed, uri.Host, StringComparison.Ordinal);
        }

        private static bool ContainsInvisibleOrSpace(string text)
        {
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
