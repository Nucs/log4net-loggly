using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace log4net.loggly {
    internal sealed class MockLogglyClient : LogglyClient {
        private readonly int _sleepTime;
        public MockLogglyClient(Config config, HttpClient httpClient, int sleepTime) : base(config, httpClient) {
            _sleepTime = sleepTime;
        }
        public MockLogglyClient(Config config, int sleepTime) : base(config) {
            _sleepTime = sleepTime;
        }

        protected override async Task<HttpResponseMessage> SendMessageAsync(string url, ReadOnlyMemoryContent content) {
            await Task.Delay(_sleepTime);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}