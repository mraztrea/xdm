using System;
using System.Threading;
using XDM.Core.Downloader;

namespace XDM.Core.MediaProcessor
{
    public abstract class BaseMediaProcessor
    {
        protected readonly ProgressResultEventArgs progressResult = new ProgressResultEventArgs();
        public abstract MediaProcessingResult MergeAudioVideStream(string file1, string file2, string outfile, CancelFlag cancellationToken, out long outFileSize);
        public abstract MediaProcessingResult MergeHLSAudioVideStream(string segmentListFile, string outfile, CancelFlag cancellationToken, out long outFileSize);
        public abstract MediaProcessingResult ConvertToMp3Audio(string segmentListFile, string outfile, CancelFlag cancellationToken, out long outFileSize);

        /// <summary>
        /// Reason reported by the backend for the last failed operation (e.g. ffmpeg exit code and
        /// the last ffmpeg log line). Null when the last operation succeeded or was cancelled.
        /// </summary>
        public string? LastError { get; protected set; }
        
        public virtual event EventHandler<ProgressResultEventArgs> ProgressChanged;

        protected void UpdateProgress(int progress)
        {
            progressResult.Progress = progress;
            ProgressChanged?.Invoke(this, progressResult);
        }
    }

    public enum MediaProcessingResult
    {
        Success,
        AppNotFound,
        Failed
    }
}
