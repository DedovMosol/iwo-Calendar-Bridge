using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace IwoBridge.Ews.Tests
{
    /// <summary>
    /// Переход на резервный адрес — на настоящем стеке EWS Managed API и HTTP, без сервера Exchange: основной адрес —
    /// закрытый порт (соединение отклоняется), резервный — местный поддельный EWS, отвечающий на GetFolder.
    /// Проверяется: недоступный основной → тот же запрос по резервному и дальше работа через него; ошибка входа на
    /// основном → на резервный не переходит (повтор с тем же паролем приблизил бы блокировку учётной записи);
    /// оба недоступны → ошибка наружу, адрес не меняется.
    /// </summary>
    public sealed class EwsFailoverTests
    {
        private const string GetFolderResponse =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">"
            + "<s:Header><h:ServerVersionInfo MajorVersion=\"8\" MinorVersion=\"1\" MajorBuildNumber=\"240\" MinorBuildNumber=\"6\" "
            + "xmlns:h=\"http://schemas.microsoft.com/exchange/services/2006/types\" /></s:Header>"
            + "<s:Body><m:GetFolderResponse xmlns:m=\"http://schemas.microsoft.com/exchange/services/2006/messages\" "
            + "xmlns:t=\"http://schemas.microsoft.com/exchange/services/2006/types\"><m:ResponseMessages>"
            + "<m:GetFolderResponseMessage ResponseClass=\"Success\"><m:ResponseCode>NoError</m:ResponseCode><m:Folders>"
            + "<t:CalendarFolder><t:FolderId Id=\"AAMkAD==\" ChangeKey=\"AgAAAA==\" /><t:TotalCount>7</t:TotalCount></t:CalendarFolder>"
            + "</m:Folders></m:GetFolderResponseMessage></m:ResponseMessages></m:GetFolderResponse></s:Body></s:Envelope>";

        [Fact]
        public async System.Threading.Tasks.Task PrimaryUnreachable_SameRequestGoesToAlternate_AndStaysThere()
        {
            using (var alternate = new FakeEws(200, GetFolderResponse))
            {
                var gateway = new EwsCalendarGateway(new[] { Service(ClosedPortUrl()), Service(alternate.Url) }, null);

                Assert.Equal(7, await gateway.CountItemsAsync(CancellationToken.None));
                Assert.Equal(1, gateway.ActiveEndpointIndex);

                Assert.Equal(7, await gateway.CountItemsAsync(CancellationToken.None));
                Assert.Equal(2, alternate.Requests);
                Assert.Equal(1, gateway.ActiveEndpointIndex);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task PrimaryRejectsLogin_NoFailover()
        {
            using (var primary = new FakeEws(401, string.Empty))
            using (var alternate = new FakeEws(200, GetFolderResponse))
            {
                var gateway = new EwsCalendarGateway(new[] { Service(primary.Url), Service(alternate.Url) }, null);

                Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => gateway.CountItemsAsync(CancellationToken.None));

                Assert.Equal(Core.Errors.OperationErrorKind.AuthenticationFailed, EwsTarget.Classify(ex));
                Assert.Equal(1, primary.Requests);
                Assert.Equal(0, alternate.Requests);
                Assert.Equal(0, gateway.ActiveEndpointIndex);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task BothUnreachable_ErrorSurfaces_ActiveUnchanged()
        {
            var gateway = new EwsCalendarGateway(new[] { Service(ClosedPortUrl()), Service(ClosedPortUrl()) }, null);

            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => gateway.CountItemsAsync(CancellationToken.None));

            Assert.Equal(Core.Errors.OperationErrorKind.ConnectionReset, EwsTarget.Classify(ex));
            Assert.Equal(0, gateway.ActiveEndpointIndex);
        }

        [Fact]
        public void NoServices_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new EwsCalendarGateway(Array.Empty<ExchangeService>(), null));
        }

        private static ExchangeService Service(Uri url) => new ExchangeService(ExchangeVersion.Exchange2007_SP1, TimeZoneInfo.Utc)
        {
            Url = url,
            Credentials = new WebCredentials("user", "secret", "EXAMPLE"),
            Timeout = 10000,
        };

        /// <summary>Порт, на котором никто не слушает: соединение отклоняется сразу (как недоступный адрес).</summary>
        private static Uri ClosedPortUrl()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return new Uri("http://127.0.0.1:" + port + "/EWS/Exchange.asmx");
        }

        /// <summary>
        /// Поддельный EWS на местном порту: на каждый запрос — заданный код и тело. HTTP без TLS: здесь проверяется
        /// переход между адресами, а не сертификаты (их проверяют тесты валидатора).
        /// </summary>
        private sealed class FakeEws : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly int _status;
            private readonly byte[] _body;
            private readonly Task _loop;
            private int _requests;

            public FakeEws(int status, string body)
            {
                _status = status;
                _body = Encoding.UTF8.GetBytes(body);
                _listener.Start();
                Url = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/EWS/Exchange.asmx");
                _loop = Task.Run(ServeAsync);
            }

            public Uri Url { get; }

            public int Requests => Volatile.Read(ref _requests);

            public void Dispose()
            {
                _listener.Stop();
                try
                {
                    _loop.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Остановка слушателя обрывает ожидание соединения — это и есть выход из цикла.
                }
            }

            private async Task ServeAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
                    {
                        return;
                    }

                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    {
                        // Соединение keep-alive: на нём может прийти несколько запросов подряд.
                        while (await HandleRequestAsync(stream).ConfigureAwait(false))
                        {
                        }
                    }
                }
            }

            private async Task<bool> HandleRequestAsync(NetworkStream stream)
            {
                string? headers = await ReadHeadersAsync(stream).ConfigureAwait(false);
                if (headers is null)
                {
                    return false;
                }

                if (headers.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] proceed = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(proceed, 0, proceed.Length).ConfigureAwait(false);
                }

                int length = ContentLength(headers);
                var buffer = new byte[4096];
                for (int read = 0; read < length;)
                {
                    int n = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, length - read)).ConfigureAwait(false);
                    if (n == 0)
                    {
                        return false;
                    }

                    read += n;
                }

                Interlocked.Increment(ref _requests);
                string reason = _status == 200 ? "OK" : "Unauthorized";
                byte[] head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + _status + " " + reason + "\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: "
                    + _body.Length + "\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                await stream.WriteAsync(_body, 0, _body.Length).ConfigureAwait(false);
                return true;
            }

            private static async Task<string?> ReadHeadersAsync(NetworkStream stream)
            {
                var text = new StringBuilder();
                var one = new byte[1];
                while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int n;
                    try
                    {
                        n = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        return null;
                    }

                    if (n == 0)
                    {
                        return null;
                    }

                    text.Append((char)one[0]);
                }

                return text.ToString();
            }

            private static int ContentLength(string headers)
            {
                foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        return int.Parse(line.Substring("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                return 0;
            }
        }
    }
}
