using XDM.Core;

namespace XDM.Core.Downloader.Adaptive
{
    public interface ICancelRequster
    {
        public ErrorCode Error { get; }

        /// <summary>
        /// Underlying reason for <see cref="Error"/> (last exception message), null when unknown.
        /// </summary>
        public string? ErrorDetail { get; }

        void CancelWithFatal(ErrorCode error, string? detail = null);
        void NotifyTransientFailure(string? detail = null);
        void RegisterThread(HttpChunkDownloader chunkDownloader);
        void UnRegisterThread(HttpChunkDownloader chunkDownloader);
        void CancelAll();
    }
}
