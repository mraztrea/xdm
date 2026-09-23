using System;
using XDM.Core;

namespace XDM.Core.Downloader
{
    public class DownloadFailedEventArgs : EventArgs
    {
        public DownloadFailedEventArgs(ErrorCode errorCode) : this(errorCode, null)
        {
        }

        public DownloadFailedEventArgs(ErrorCode errorCode, string? detail)
        {
            ErrorCode = errorCode;
            Detail = detail;
        }

        public ErrorCode ErrorCode { get; }

        /// <summary>
        /// Underlying reason reported by the failing stage (exception message, target file, ffmpeg output).
        /// Null when the failing stage had no additional information.
        /// </summary>
        public string? Detail { get; }
    }
}
