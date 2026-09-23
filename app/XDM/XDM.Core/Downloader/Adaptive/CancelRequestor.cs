using System.Collections.Generic;
using XDM.Core;

namespace XDM.Core.Downloader.Adaptive
{
    class CancelRequestor : ICancelRequster
    {
        private List<HttpChunkDownloader> downloaders = new();
        private CancelFlag _cancellationToken;
        public ErrorCode Error { get; private set; } = ErrorCode.None;
        public string? ErrorDetail { get; private set; }

        public CancelRequestor(CancelFlag cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public void CancelWithFatal(ErrorCode error, string? detail = null)
        {
            CancelAll();
            this.Error = error;
            this.ErrorDetail = detail;
            if (!_cancellationToken.IsCancellationRequested)
            {
                _cancellationToken.Cancel();
            }
        }

        public void NotifyTransientFailure(string? detail = null)
        {
            lock (this)
            {
                foreach (var downloader in downloaders)
                {
                    if (!downloader.TransientFailure)
                    {
                        return;
                    }
                }
                CancelWithFatal(ErrorCode.MaxRetryFailed, detail);
            }
        }

        public void RegisterThread(HttpChunkDownloader chunkDownloader)
        {
            lock (this)
            {
                downloaders.Add(chunkDownloader);
            }
        }

        public void UnRegisterThread(HttpChunkDownloader chunkDownloader)
        {
            lock (this)
            {
                downloaders.Remove(chunkDownloader);
            }
        }

        public void CancelAll()
        {
            lock (this)
            {
                var list = new List<HttpChunkDownloader>(downloaders);
                foreach (var downloader in list)
                {
                    downloader.Cancel();
                }
            }
        }
    }
}
