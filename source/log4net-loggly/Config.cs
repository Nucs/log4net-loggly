using System;

namespace log4net.loggly {
    /// <summary>
    /// Class holding configuration for this library
    /// </summary>
    internal class Config {
        private string _logicalThreadContextKeys;
        internal string[] LogicalThreadContextKeysSplit;
        private string _globalContextKeys;
        internal string[] GlobalContextKeysSplit;

        public Config() {
            UserAgent = "loggly-log4net-appender";
            TimeoutInSeconds = 30;
            MaxSendRetries = 3;
            Tag = "log4net";
            LogicalThreadContextKeys = null;
            GlobalContextKeys = null;
            BufferSize = 500;
            NumberOfInnerExceptions = 4;
            SendInterval = TimeSpan.FromSeconds(5);
            FinalFlushWaitTime = TimeSpan.FromSeconds(10);
            PassivelyFlushEvery = TimeSpan.FromMinutes(2);

            // Limitation of HTTP endpoint is 1MB per event, 5MB per bulk:
            // https://www.loggly.com/docs/http-endpoint/ and https://www.loggly.com/docs/http-bulk-endpoint/

            // max 5MB per bulk (real 5*1024*1024 is still rejected so stay a bit under the limit)
            MaxBulkSizeBytes = 4717712; // 4.5mb
            // Real 1024*1024 is still too much for HTTP endpoint so let's stay on safe side with 1000*1000
            MaxEventSizeBytes = 1000000;
        }

        /// <summary>
        /// Max size of whole event sent to Loggly in bytes
        /// </summary>
        public int MaxEventSizeBytes { get; set; }

        /// <summary>
        /// Max size ot one bulk of events sent to Loggly in bytes
        /// </summary>
        public int MaxBulkSizeBytes { get; set; }

        /// <summary>
        /// URL where the logs are sent
        /// </summary>
        public string RootUrl { get; set; }

        /// <summary>
        /// Customer token used to send the logs
        /// </summary>
        public string CustomerToken { get; set; }

        /// <summary>
        /// User agent string used when sending the logs
        /// </summary>
        public string UserAgent { get; set; }

        /// <summary>
        /// Tag or tags separated by commas
        /// </summary>
        public string Tag { get; set; }

        /// <summary>
        /// Comma separated list of keys to LogicalThreadContext whose values will be added to log.
        /// </summary>
        public string LogicalThreadContextKeys {
            get => _logicalThreadContextKeys;
            set {
                _logicalThreadContextKeys = value;
                LogicalThreadContextKeysSplit = value?.Split(',');
            }
        }

        /// <summary>
        /// Comma separated list of keys to GlobalContext whose values will be added to log.
        /// </summary>
        public string GlobalContextKeys {
            get => _globalContextKeys;
            set {
                _globalContextKeys = value;
                GlobalContextKeysSplit = value?.Split(',');
            }
        }

        /// <summary>
        /// Size of sending buffer
        /// </summary>
        public int BufferSize { get; set; }

        /// <summary>
        /// How many inner exceptions should be sent to Loggly
        /// </summary>
        public int NumberOfInnerExceptions { get; set; }

        /// <summary>
        /// How often should the events buffer be sent if it's not yet full
        /// </summary>
        public TimeSpan SendInterval { get; set; }

        /// <summary>
        /// How long to wait during final appender flush until all messages are flushed.
        /// </summary>
        public TimeSpan FinalFlushWaitTime { get; set; }

        /// <summary>
        /// How often should the events buffer be sent if it's not yet full. If null, then it's not used.
        /// </summary>
        public TimeSpan? PassivelyFlushEvery { get; set; }

        /// <summary>
        /// Request timeout when sending logs to Loggly
        /// </summary>
        public int TimeoutInSeconds { get; set; }

        /// <summary>
        /// How many times library tries to send logs to Loggly before giving up and trying next batch.
        /// </summary>
        public int MaxSendRetries { get; set; }

        /// <summary>
        /// If true, then thread information will be included in the log.
        /// </summary>
        /// <remarks>Useless in a async-await task-based system like ASP.NET, therefore false by default</remarks>
        public bool IncludeThreadInformation { get; set; }
    }
}