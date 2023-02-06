using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace log4net.loggly {
    internal class LogglyAsyncBuffer : ILogglyAsyncBuffer, IDisposable {
        private readonly Config _config;
        private readonly ConcurrentQueue<string> _messages = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0, 1);
        private readonly ManualResetEventSlim _flushCompletion = new ManualResetEventSlim(false);
        private readonly ILogglyClient _client;
        private readonly CancellationTokenSource _cancellation;
        private static readonly Encoding _encoding = Encoding.UTF8;
        private static readonly byte[] _newLine;
        private int _messagesCount;
        private Exception _innerException;

        static LogglyAsyncBuffer() {
            _newLine = _encoding.GetBytes(Environment.NewLine);
        }

        public LogglyAsyncBuffer(Config config, ILogglyClient client) {
            _config = config;
            _client = client;
            _cancellation = new CancellationTokenSource();
            _ = Task.Run(LogglySendBufferTask, _cancellation.Token);
            if (config.PassivelyFlushEvery.HasValue) {
                _config.PassivelyFlushEvery = TimeSpan.FromSeconds(Math.Max(10, _config.PassivelyFlushEvery!.Value.TotalSeconds));
                _ = Task.Run(LogglyTimedSendTask, _cancellation.Token);
            }

            var appDomain = AppDomain.CurrentDomain;
            if (appDomain != null) //unit test env
                AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
        }

        private async Task LogglyTimedSendTask() {
            //make sure it is atleast every 10s
            while (!_cancellation.IsCancellationRequested) {
                try {
                    //sleep for configured time
                    await Task.Delay(_config.PassivelyFlushEvery.Value!, _cancellation.Token);
                } catch (TaskCanceledException) {
                    break;
                } catch (OperationCanceledException) {
                    break;
                }

                //only if we have pending items
                if (_messagesCount == 0)
                    continue;

                //signal the buffer to flush
                try {
                    _semaphore.Release(1);
                } catch (SemaphoreFullException) { }
            }
        }

        /// <summary>
        /// Buffer message to be sent to Loggly
        /// </summary>
        public void BufferForSend(string message) {
            if (_cancellation.IsCancellationRequested)
                throw new OperationCanceledException($"Unable to log to loggly because operation was cancelled due to previous error or disposal. See inner exception for details.", _innerException, _cancellation.Token);

            _messages.Enqueue(message);

            // increment the count of messages in the buffer and release the semaphore if we have enough messages
            if (Interlocked.Increment(ref _messagesCount) % _config.BufferSize == 0) {
                try {
                    _semaphore.Release(1);
                } catch (SemaphoreFullException) { }
            }
        }

        /// <summary>
        /// Flush any buffered messages right now.
        /// This method returns once all messages are flushed or when timeout expires.
        /// If new messages are coming during flush they will be included and may delay flush operation.
        /// </summary>
        public bool Flush(TimeSpan maxWait) {
            if (_cancellation.IsCancellationRequested)
                throw new OperationCanceledException($"Unable to log to loggly because operation was cancelled due to previous error or disposal. See inner exception for details.", _innerException, _cancellation.Token);

            if (_messagesCount == 0)
                return true;

            try {
                _semaphore.Release(1);
            } catch (SemaphoreFullException) { }

            return maxWait.Ticks == 0 || //TimeSpan.Zero
                   _flushCompletion.Wait(maxWait);
        }

        private async Task LogglySendBufferTask() {
            try {
                var bufferSize = _config.BufferSize;
                var cancelToken = _cancellation.Token;
                string nextMessage = null; //holder for a message that is too large for the current batch to avoid double lookup of _messages
                DateTime _lastSend = DateTime.MinValue;
                using MemoryStream ms = new MemoryStream(_config.MaxBulkSizeBytes);
                bool cancelling = false;

                while (true) {
                    try {
                        //await buffer to fill up
                        await _semaphore.WaitAsync(cancelToken).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        //we ignore cancellation to sumbit remainder
                        cancelling = true;
                    }

                    while (_messagesCount > 0) {
                        try {
                            //handle send interval
                            var now = DateTime.UtcNow;
                            if (_lastSend > now) {
                                await Task.Delay(_lastSend - now, cancelToken).ConfigureAwait(false);
                            }

                            _lastSend = now + _config.SendInterval;
                        } catch (OperationCanceledException) {
                            //we ignore cancellation to sumbit remainder
                            cancelling = true;
                        }

                        int sendBufferIndex = 0;
                        while (sendBufferIndex < bufferSize //iterate buffer size
                               && (nextMessage != null || _messages.TryDequeue(out nextMessage)) //get next message or last too large message
                               && (ms.Length == 0 || ms.Length + nextMessage.Length <= _config.MaxBulkSizeBytes)) { //first message or fits in bulk

                            // peek/dequeue happens only in one thread so what we peeked above is what we dequeue here

                            sendBufferIndex++;
                            WriteMessage(nextMessage, ms, sendBufferIndex >= bufferSize);

                            //reset next message, if won't be null for next iteration if we have a message that is too large for the current batch
                            nextMessage = null;
                        }

                        if (sendBufferIndex > 0) {
                            //update messages count
                            Interlocked.Add(ref _messagesCount, -sendBufferIndex); //remove messages count to reset triggers

                            try {
                                await _client.SendAsync(ms).ConfigureAwait(false);
                            } catch (HttpRequestException e) {
                                //incase of token exception
                                _innerException = e;
                                Dispose();
                                return;
                            } finally {
                                ms.SetLength(0); //clear buffer
                            }
                        }
                    }

                    _flushCompletion.Set(); //signal all flush waiters
                    _flushCompletion.Reset(); //reset for next flush waiters

                    if (cancelling)
                        break;
                }
            } catch (Exception e) {
                _innerException = e;
            }
        }

        #if !NETSTANDARD2_1
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        #endif
        private void WriteMessage(string nextMessage, MemoryStream ms, bool isLast) {
            var size = _encoding.GetByteCount(nextMessage);
            Span<byte> buffer = stackalloc byte[size + (isLast ? 0 : _newLine.Length)];
            if (!isLast) {
                var written = _encoding.GetBytes(nextMessage, buffer);
                for (int i = 0; i < _newLine.Length; i++)
                    buffer[written + i] = _newLine[i];

                ms.Write(buffer.Slice(0, written + _newLine.Length));
            } else {
                var written = _encoding.GetBytes(nextMessage, buffer);
                ms.Write(buffer.Slice(0, written));
            }
        }

        private void CurrentDomainOnProcessExit(object sender, EventArgs e) {
            try {
                if (_messagesCount > 0) {
                    _semaphore.Release(1);
                    Flush(TimeSpan.FromSeconds(10));
                }
            } catch (Exception) {
                // ignored
            }
        }

        public void Dispose() {
            try {
                _cancellation.Cancel();
                _cancellation.Dispose();
            } catch (ObjectDisposedException) { }

            try {
                _flushCompletion.Dispose();
            } catch (ObjectDisposedException) { }

            //not disposing _client due to possability of late send

            var appDomain = AppDomain.CurrentDomain;
            if (appDomain != null) //unit test env
                AppDomain.CurrentDomain.ProcessExit -= CurrentDomainOnProcessExit;
        }
    }
}