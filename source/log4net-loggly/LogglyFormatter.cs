using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace log4net.loggly
{
    internal class LogglyFormatter : ILogglyFormatter
    {
        private static readonly string _machineName;
        private readonly Config _config;
        private static readonly string _currentProcessName;

        private static readonly JsonSerializer _jsonSerializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
        {
            PreserveReferencesHandling = PreserveReferencesHandling.Arrays,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        });
        
        private static readonly JsonSerializerSettings _jObjectJsonSerializerSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };
        
        private static readonly JsonMergeSettings _mergeSettings = new JsonMergeSettings
        {
            MergeArrayHandling = MergeArrayHandling.Union
        };

        static LogglyFormatter() {
            _machineName = Environment.MachineName;
            _currentProcessName = Process.GetCurrentProcess().ProcessName;
        }
        
        public LogglyFormatter(Config config)
        {
            _config = config;
        }

        public string ToJson(LoggingEvent loggingEvent, string renderedMessage)
        {
            // formatting base logging info
            JObject loggingInfo = new JObject {
                ["timestamp"] = loggingEvent.TimeStamp.ToString(@"yyyy-MM-ddTHH\:mm\:ss.fffzzz"),
                ["level"] = loggingEvent.Level?.DisplayName,
                ["hostName"] = _machineName,
                ["process"] = _currentProcessName,
                ["threadName"] = loggingEvent.ThreadName,
                ["loggerName"] = loggingEvent.LoggerName
            };

            AddMessageOrObjectProperties(loggingInfo, loggingEvent, renderedMessage);
            AddExceptionIfPresent(loggingInfo, loggingEvent);
            AddContextProperties(loggingInfo, loggingEvent);

            string resultEvent = ToJsonString(loggingInfo);

            int eventSize = Encoding.UTF8.GetByteCount(resultEvent);

            // Be optimistic regarding max event size, first serialize and then check against the limit.
            // Only if the event is bigger than allowed go back and try to trim exceeding data.
            if (eventSize > _config.MaxEventSizeBytes)
            {
                int bytesOver = eventSize - _config.MaxEventSizeBytes;
                // ok, we are over, try to look at plain "message" and cut that down if possible
                if (loggingInfo["message"] != null)
                {
                    var fullMessage = loggingInfo["message"].Value<string>();
                    var originalMessageLength = fullMessage.Length;
                    var newMessageLength = Math.Max(0, originalMessageLength - bytesOver);
                    loggingInfo["message"] = fullMessage.Substring(0, newMessageLength);
                    bytesOver -= originalMessageLength - newMessageLength;
                }

                // Message cut and still over? We can't shorten this event further, drop it,
                // otherwise it will be rejected down the line anyway and we won't be able to identify it so easily.
                if (bytesOver > 0)
                {
                    ErrorReporter.ReportError(
                        $"LogglyFormatter: Dropping log event exceeding allowed limit of {_config.MaxEventSizeBytes} bytes. " +
                        $"First 500 bytes of dropped event are: {resultEvent.Substring(0, Math.Min(500, resultEvent.Length))}");
                    return null;
                }

                resultEvent = ToJsonString(loggingInfo);
            }

            return resultEvent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ToJsonString(JObject loggingInfo)
        {
            return JsonConvert.SerializeObject(loggingInfo, _jObjectJsonSerializerSettings);
        }

        private void AddContextProperties(JObject loggingInfo, LoggingEvent loggingEvent)
        {
            if (_config.GlobalContextKeys != null)
            {
                var properties = GlobalContext.Properties;
                foreach (var key in _config.GlobalContextKeysSplit)
                {
                    if (TryGetPropertyValue(properties[key], out var propertyValue))
                    {
                        loggingInfo[key] = JToken.FromObject(propertyValue);
                    }
                }
            }
            
            if (_config.IncludeThreadInformation) {
                var threadProperties = ThreadContext.Properties;
                var threadContextProperties = threadProperties.GetKeys();
                if (threadContextProperties != null && threadContextProperties.Length > 0) {
                    foreach (var key in threadContextProperties) {
                        if (TryGetPropertyValue(threadProperties[key], out var propertyValue)) {
                            loggingInfo[key] = JToken.FromObject(propertyValue);
                        }
                    }
                }

                if (_config.LogicalThreadContextKeys != null) {
                    var properties = LogicalThreadContext.Properties;
                    foreach (var key in _config.LogicalThreadContextKeysSplit) {
                        if (TryGetPropertyValue(properties[key], out var propertyValue)) {
                            loggingInfo[key] = JToken.FromObject(propertyValue);
                        }
                    }
                }
            }
            
            var loggingEventProperties = loggingEvent.GetProperties();
            if (loggingEventProperties.Count > 0)
            {
                foreach (DictionaryEntry property in loggingEventProperties)
                {
                    if (TryGetPropertyValue(property.Value, out var propertyValue))
                    {
                        loggingInfo[(string)property.Key] = JToken.FromObject(propertyValue);
                    }
                }
            }
        }

        private void AddExceptionIfPresent(JObject loggingInfo, LoggingEvent loggingEvent)
        {
            JObject exceptionInfo = GetExceptionInfo(loggingEvent);
            if (exceptionInfo != null)
            {
                loggingInfo["exception"] = exceptionInfo;
            }
        }

        private void AddMessageOrObjectProperties(JObject loggingInfo, LoggingEvent loggingEvent, string renderedMessage)
        {
            if (loggingEvent.MessageObject is string messageString)
            {
                if (messageString.IndexOf('{', 0, Math.Min(20, messageString.Length)) != -1) // quick check if there is a chance of JSON
                {
                    // try parse as JSON, otherwise use rendered message passed to this method
                    try
                    {
                        var json = JObject.Parse(messageString);
                        loggingInfo.Merge(json,_mergeSettings);
                        // we have all we need
                        return;
                    }
                    catch (JsonReaderException)
                    {
                        // no JSON, handle it as plain string
                    }
                }

                // plain string, use rendered message
                loggingInfo["message"] = NormalizeNull(renderedMessage);
            }
            else if (loggingEvent.MessageObject == null
                    // log4net.Util.SystemStringFormat is object used when someone calls log.*Format(...)
                    // and in that case renderedMessage is what we want
                    || loggingEvent.MessageObject is Util.SystemStringFormat
                    // legacy code, it looks that there are cases when the object is StringFormatFormattedMessage,
                    // but then it should be already in renderedMessage
                    || (loggingEvent.MessageObject.GetType().FullName?.Contains("StringFormatFormattedMessage") ?? false))
            {
                loggingInfo["message"] = NormalizeNull(renderedMessage);
            }
            else
            {
                // serialize object to JSON and add it's properties to loggingInfo
                var json = JObject.FromObject(loggingEvent.MessageObject, _jsonSerializer);
                loggingInfo.Merge(json, _mergeSettings);
            }
        }

        private static bool TryGetPropertyValue(object property, out object propertyValue)
        {
            if (property is IFixingRequired fixedProperty && fixedProperty.GetFixedObject() != null)
            {
                propertyValue = fixedProperty.GetFixedObject();
            }
            else
            {
                propertyValue = property;
            }

            return propertyValue != null;
        }

        /// <summary>
        /// Returns the exception information. Also takes care of the InnerException.
        /// </summary>
        /// <param name="loggingEvent"></param>
        /// <returns></returns>
        private JObject GetExceptionInfo(LoggingEvent loggingEvent)
        {
            if (loggingEvent.ExceptionObject == null)
            {
                return null;
            }

            return GetExceptionInfo(loggingEvent.ExceptionObject, _config.NumberOfInnerExceptions);
        }

        /// <summary>
        /// Return exception as JObject
        /// </summary>
        /// <param name="exception">Exception to serialize</param>
        /// <param name="deep">The number of inner exceptions that should be included.</param>
        private JObject GetExceptionInfo(Exception exception, int deep)
        {
            if (exception == null || deep < 0)
                return null;

            var result = new JObject
            {
                ["exceptionType"] = exception.GetType().FullName,
                ["exceptionMessage"] = exception.Message,
                ["stacktrace"] = exception.StackTrace,
                ["innerException"] = deep-- > 0 ? GetExceptionInfo(exception.InnerException, deep) : null
            };
            if (!result["innerException"].HasValues)
            {
                result.Remove("innerException");
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string NormalizeNull(string? value)
        {
            return !string.IsNullOrEmpty(value) ? value : "null";
        }
    }
}
