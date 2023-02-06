using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace log4net.loggly {
    internal class LogglyClient : ILogglyClient {
        private readonly Config _config;
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private const string BulkPath = "bulk/";

        public LogglyClient(Config config, HttpClient httpClient) {
            _config = config;
            _httpClient = httpClient;
            _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutInSeconds);
            _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(config.UserAgent);
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _url = BuildUrl(config);
        }

        public LogglyClient(Config config) : this(config, new HttpClient()) { }

        public async ValueTask SendAsync(MemoryStream messagesBuffer) {
            int currentRetry = 0;
            // setting MaxSendRetries means that we retry forever, we never throw away logs without delivering them
            var content = new ReadOnlyMemoryContent(messagesBuffer.GetBuffer().AsMemory(0, (int) messagesBuffer.Length));
            while (_config.MaxSendRetries < 0 || currentRetry <= _config.MaxSendRetries) {
                HttpStatusCode statusCode = default;
                try {
                    var responseMessage = await SendMessageAsync(_url, content);
                    statusCode = responseMessage.StatusCode;
                    responseMessage.EnsureSuccessStatusCode();
                    break; //no retry needed
                } catch (HttpRequestException e) {
                    if (statusCode == HttpStatusCode.Forbidden) {
                        ErrorReporter.ReportError($"LogglyClient: Provided Loggly customer token " +
                                                  $"'{(string.IsNullOrEmpty(_config.CustomerToken) ? "" : (new string(_config.CustomerToken.Take(5).ToArray())) + new string('*', Math.Max(3, _config.CustomerToken.Length - 5)))}'" +
                                                  $" is invalid. No logs will be sent to Loggly.");
                        throw; //caught later and triggers disposal of logger appender. pipes the exception to the user.
                    } else {
                        ErrorReporter.ReportError($"LogglyClient: Error sending logs to Loggly: {statusCode.ToString()}, {e.Message}");
                    }

                    currentRetry++;
                    if (currentRetry > _config.MaxSendRetries) {
                        //print it at the top and bottom to ensure it is caught.
                        ErrorReporter.ReportError($"LogglyClient: Maximal number of retries ({_config.MaxSendRetries}) reached. Discarding current batch of logs and moving on to the next one.");
                        ErrorReporter.Dump($"\nMessages:\n{Encoding.UTF8.GetString(messagesBuffer.GetBuffer(), 0, (int) messagesBuffer.Length)}");
                        ErrorReporter.ReportError($"LogglyClient: Maximal number of retries ({_config.MaxSendRetries}) reached. Discarding current batch of logs and moving on to the next one.");
                    }
                }
            }
        }

        protected virtual Task<HttpResponseMessage> SendMessageAsync(string url, ReadOnlyMemoryContent content) {
            return _httpClient.PostAsync(url, content);
        }

        private static string BuildUrl(Config config) {
            string tag = config.Tag;

            // keeping userAgent backward compatible
            if (!string.IsNullOrWhiteSpace(config.UserAgent)) {
                tag = tag + "," + config.UserAgent;
            }

            StringBuilder sb = new StringBuilder(config.RootUrl);
            if (sb.Length > 0 && sb[sb.Length - 1] != '/') {
                sb.Append("/");
            }

            sb.Append(BulkPath);
            sb.Append(config.CustomerToken);
            sb.Append("/tag/");
            sb.Append(tag);
            return sb.ToString();
        }

        public void Dispose() {
            _httpClient.Dispose();
        }
    }
}