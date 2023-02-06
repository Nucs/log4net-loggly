using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace log4net.loggly
{
    internal interface ILogglyClient : IDisposable
    {
        /// <summary>
        /// Send array of messages to Loggly
        /// </summary>
        /// <param name="messagesBuffer">Buffer containing messages to send. Buffer does not have to be full.
        /// Number of valid messages in buffer is passed via <paramref name="numberOfMessages"/> parameters.
        /// Anything past this number should be ignored.
        /// </param>
        /// <exception cref="HttpRequestException">When the loggly token is invalid</exception>
        ValueTask SendAsync(MemoryStream messagesBuffer);
    }
}