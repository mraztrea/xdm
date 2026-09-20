using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using TraceLog;
using XDM.Core;
using XDM.Core.Util;
using XDM.Core.Clients.Http;

namespace XDM.Core.Downloader.Adaptive
{
    public class HttpChunkDownloader
    {
        protected readonly Chunk _chunk;
        protected readonly IHttpClient _http;
        protected readonly CancelFlag _cancellationToken = new();
        protected readonly IChunkStreamMap _chunkStreamMap;
        protected readonly ICancelRequster _cancelRequster;
        protected readonly EventArgs EmptyArgs = EventArgs.Empty;
        protected long _lastUpdated;
        protected ChunkDownloadedEventArgs downloadedEventArgs;
        protected ManualResetEvent sleepHandle = new ManualResetEvent(false);
        protected Dictionary<string, List<string>> headers;
        protected string cookies;
        protected AuthenticationInfo? authentication;

        public event EventHandler<ChunkDownloadedEventArgs>? ChunkDataReceived;
        public event EventHandler<MimeTypeReceivedEventArgs>? MimeTypeReceived;

        public HttpChunkDownloader(
            Chunk chunk,
            IHttpClient http,
            Dictionary<string, List<string>> headers,
            string cookies,
            AuthenticationInfo? authentication,
            IChunkStreamMap chunkStreamMap,
            ICancelRequster cancelRequster)
        {
            _chunk = chunk;
            _http = http;
            _chunkStreamMap = chunkStreamMap;
            _cancelRequster = cancelRequster;
            _lastUpdated = Helpers.TickCount();
            downloadedEventArgs = new();

            this.cookies = cookies;
            this.headers = headers;
            this.authentication = authentication;
        }

        public bool TransientFailure { get; set; }

        protected virtual Stream PrepareOutStream()
        {
            var targetStream = new FileStream(_chunkStreamMap.GetStream(_chunk.Id),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            //since we store each segments in separate file irrespective of byte ranged or not, no need to care about offset

            //The file on disk is authoritative: after a crash/SIGKILL the recorded offset can be ahead
            //of the flushed bytes (which would leave a zero filled hole in the final file) or behind
            //them (which would duplicate a tail), so the file is cut back to min(recorded, length) and
            //the download continues from there. That is also why the persisted count does not need a
            //flush after every write.
            var end = Math.Min(_chunk.Downloaded, targetStream.Length);
            targetStream.SetLength(end);
            targetStream.Seek(end, SeekOrigin.Begin);
            _chunk.Downloaded = end;
            return targetStream;
        }

        public void Download()
        {
            _cancelRequster.RegisterThread(this);
            var retryCount = 0;
#if NET35
            var buffer = new byte[32 * 1024];
#else
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32 * 1024);
#endif
            try
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        Log.Debug("Creating request");
                        var request = this._http.CreateGetRequest(_chunk.Uri, this.headers, this.cookies, this.authentication);
                        //                    HttpRequestMessage request = new HttpRequestMessage
                        //                    {
                        //                        Method = HttpMethod.Get,
                        //                        RequestUri = _chunk.Uri,
                        //#if NET5_0
                        //                        Version = HttpVersion.Version20
                        //#endif
                        //                    };
                        if (_chunk.Size > 0) //size of hls chunk without byte range size will be -1
                        {
                            request.AddRange(_chunk.Offset + _chunk.Downloaded, _chunk.Offset + _chunk.Size - 1);
                            // request.Headers.Range = new RangeHeaderValue(_chunk.Offset + _chunk.Downloaded,
                            //_chunk.Size > 0 ? _chunk.Offset + _chunk.Size - 1 : null);
                        }
                        else
                        {
                            request.AddRange(_chunk.Offset + _chunk.Downloaded);
                        }

                        Log.Debug("Sending request");
                        using var response = this._http.Send(request);
                        Log.Debug("Sent request");
                        _cancellationToken.ThrowIfCancellationRequested();
                        response.EnsureSuccessStatusCode();
                        //using var response = await _http
                        //    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken)
                        //    .ConfigureAwait(false);
                        TransientFailure = false;
                        retryCount = 0;

                        if (response.StatusCode != HttpStatusCode.PartialContent && (response.StatusCode != HttpStatusCode.OK && _chunk.Downloaded + _chunk.Offset > 0))
                        {
                            //throw new Exception(response.ReasonPhrase);
                            _cancelRequster.CancelWithFatal(ErrorCode.InvalidResponse);
                            return;
                        }

                        if (_chunk.Downloaded > 0 && response.StatusCode == HttpStatusCode.OK)
                        {
                            Log.Debug("Partial content non supported, discarding partially downloaded parts");
                            _chunk.Downloaded = 0;
                        }

                        if (response.ContentType != null)
                        {
                            MimeTypeReceived?.Invoke(this, new MimeTypeReceivedEventArgs
                            {
                                MimeType = response.ContentType,
                                Chunk = _chunk
                            });
                        }
                        Log.Debug("Init download request");
                        var stream = response.GetResponseStream();
                        _cancellationToken.ThrowIfCancellationRequested();
                        using var sourceStream = stream;
                        using var targetStream = PrepareOutStream();

                        while (!_cancellationToken.IsCancellationRequested)
                        {
                            int x = sourceStream.Read(buffer, 0, buffer.Length);
                            _cancellationToken.ThrowIfCancellationRequested();
                            if (x == 0)
                            {
                                //a byte ranged chunk that ends short of its declared length is a truncated
                                //response, not a finished segment: fail it as transient so the retry loop
                                //re-requests the missing bytes instead of concatenating a corrupt segment
                                if (_chunk.Size > 0 && _chunk.Downloaded < _chunk.Size)
                                {
                                    Log.Debug("Short chunk " + _chunk.Downloaded + "/" + _chunk.Size);
                                    throw new Exception("Chunk is shorter than its declared size");
                                }
                                targetStream.Flush();
                                _chunk.ChunkState = ChunkState.Finished;
                                return;
                            }

                            targetStream.Write(buffer, 0, x);

                            _chunk.Downloaded += x;
                            downloadedEventArgs.Downloaded = x;
                            ChunkDataReceived?.Invoke(this, downloadedEventArgs);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Debug(e, "Error in DownloadAsync");
                        if (e is DirectoryNotFoundException || e is IOException)
                        {
                            _cancelRequster.CancelWithFatal(ErrorCode.DiskError);
                            return;
                        }
                        TransientFailure = true;
                        retryCount++;
                        if (retryCount > Config.Instance.MaxRetry)
                        {
                            retryCount = 0;
                            _cancelRequster.NotifyTransientFailure();
                        }
                        else
                        {
                            if (!(e is OperationCanceledException))
                            {
                                sleep(Config.Instance.RetryDelay * 1000);
                            }
                        }
                    }
                }
            }
            finally
            {
#if !NET35
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
#endif
                Log.Debug("Finished request");
                _cancelRequster.UnRegisterThread(this);
            }
        }

        private void sleep(int interval)
        {
            sleepHandle.WaitOne(interval);
        }

        public void Cancel()
        {
            _cancellationToken.Cancel();
            sleepHandle.Set();
        }
    }

    public class ChunkDownloadedEventArgs : EventArgs
    {
        public long Downloaded
        {
            get; set;
        }
    }

    public class MimeTypeReceivedEventArgs : EventArgs
    {
        public string? MimeType
        {
            get; set;
        }

        public Chunk? Chunk { get; set; }
    }
}
