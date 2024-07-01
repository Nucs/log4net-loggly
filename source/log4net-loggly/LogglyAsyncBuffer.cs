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
        private volatile int _messagesCount;
        private Exception? _innerException;
        private bool _exiting;
        private readonly Process? _currentProcess;

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

            try {
                //attempt peacefully to register for appdomain exit
                var appDomain = AppDomain.CurrentDomain;
                if (appDomain != null) //unit test env might be null
                    AppDomain.CurrentDomain.ProcessExit += OnUnexpectedExit;
            } catch (Exception) { }

            try {
                //attempt peacefully to register for process exit
                _currentProcess = Process.GetCurrentProcess();
                _currentProcess.Exited += OnUnexpectedExit;
                _currentProcess.EnableRaisingEvents = true;
            } catch (Exception) {
                //ignored
            }
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
                } catch (SemaphoreFullException) { } catch (ObjectDisposedException) {
                    break;
                }
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
            var count = Interlocked.Increment(ref _messagesCount);
            if (count % _config.BufferSize == 0) {
                try {
                    _semaphore.Release(1);
                } catch (SemaphoreFullException) { }
            } else if (count == 1) {
                _flushCompletion.Reset(); //reset for next flush waiters
            }
        }

        /// <summary>
        /// Flush any buffered messages right now.
        /// This method returns once all messages are flushed or when timeout expires.
        /// If new messages are coming during flush they will be included and may delay flush operation.
        /// </summary>
        public bool Flush(TimeSpan maxWait) {
            if (_messagesCount == 0)
                return true;

            try {
                _semaphore.Release(1);
            } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }

            try {
                return maxWait.Ticks == 0 || //TimeSpan.Zero
                       _flushCompletion.Wait(maxWait);
            } catch (ObjectDisposedException) {
                return true; //assume flush is done
            }
        }

        private async Task LogglySendBufferTask() {
            try {
                var bufferSize = _config.BufferSize;
                var cancelToken = _cancellation.Token;
                string? nextMessage = null; //holder for a message that is too large for the current batch to avoid double lookup of _messages
                var lastSend = DateTime.MinValue;
                using var ms = new MemoryStream(_config.MaxBulkSizeBytes);

                while (true) {
                    try {
                        //await buffer to fill up
                        await _semaphore.WaitAsync(cancelToken).ConfigureAwait(false);
                    } catch (OperationCanceledException) { } catch (ObjectDisposedException) {
                        //we ignore cancellation to sumbit remainder
                    }

                    while (_messagesCount > 0) {
                        try {
                            //handle send interval
                            var now = DateTime.UtcNow;
                            if (lastSend > now) {
                                await Task.Delay(lastSend - now, cancelToken).ConfigureAwait(false);
                            }

                            lastSend = now + _config.SendInterval;
                        } catch (OperationCanceledException) {
                            //we ignore cancellation to sumbit remainder
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

                    //buffer emptied
                    try {
                        _flushCompletion.Set(); //signal all flush waiters
                    } catch (ObjectDisposedException) { }

                    if (_exiting) break;
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

        private void OnUnexpectedExit(object? sender, EventArgs e) {
            if (_exiting || _messagesCount <= 0)
                return;

            try {
                _exiting = true;
                Flush(TimeSpan.FromSeconds(10));
            } catch (Exception) {
                // ignored
            }

            try {
                _flushCompletion.Dispose();
            } catch (ObjectDisposedException) { }
        }

        public void Dispose() {
            try {
                if (_innerException != null) //if not due to inner exception
                    Flush(TimeSpan.Zero);
            } catch (Exception) {
                //ignored
            }

            try {
                _cancellation.Cancel();
                _cancellation.Dispose();
            } catch (ObjectDisposedException) { }

            try {
                _semaphore.Dispose();
            } catch (ObjectDisposedException) { }

            try {
                if (!_exiting) {
                    _flushCompletion.Set();
                    _flushCompletion.Dispose();
                }
            } catch (ObjectDisposedException) { }

            //not disposing _client due to possability of late send
            try {
                var appDomain = AppDomain.CurrentDomain;
                if (appDomain != null) //unit test env
                    AppDomain.CurrentDomain.ProcessExit -= OnUnexpectedExit;
            } catch (Exception) {
                //ignored
            }

            if (_currentProcess != null) {
                try {
                    //attempt peacefully to register for process exit
                    _currentProcess.EnableRaisingEvents = false;
                    _currentProcess.Exited -= OnUnexpectedExit;
                } catch (Exception) {
                    //ignored
                }
            }

            _messages.Clear();
        }
    }
}