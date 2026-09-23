using System.Collections.Generic;
using Translations;
using XDM.Core;

#if !NET5_0_OR_GREATER
using XDM.Compatibility;
#endif

namespace XDM.Core
{
    internal static class ErrorMessages
    {
        private const string ErrorPrefix = "ERR_";
        private const string GenericErrorMessage = "Download error";

        private static readonly Dictionary<string, string> fallbackMessages = new()
        {
            [ErrorPrefix + ErrorCode.Generic] = GenericErrorMessage,
            [ErrorPrefix + ErrorCode.NonResumable] = "Download is not resumable",
            [ErrorPrefix + ErrorCode.AssemblingFailed] = "Download assembling failed",
            [ErrorPrefix + ErrorCode.MaxRetryFailed] = "Connection lost or server not responding after repeated retries",
            [ErrorPrefix + ErrorCode.InvalidResponse] = "Invalid response from server",
            [ErrorPrefix + ErrorCode.FFmpegNotFound] = "FFmpeg not found",
            [ErrorPrefix + ErrorCode.FFmpegError] = "Could not merge audio and video streams using FFmpeg",
            [ErrorPrefix + ErrorCode.DiskError] = "Not enough disk space, or disk is full or read-only",
            [ErrorPrefix + ErrorCode.SessionExpired] = "Session expired",
            [ErrorPrefix + ErrorCode.TargetFileCreateFailed] = "Could not create the output file",
            [ErrorPrefix + ErrorCode.TargetFileWriteFailed] = "Could not write to the output file"
        };

        /// <summary>
        /// Message shown for a failed download. The text for <paramref name="errorCode"/> comes from
        /// Lang/*.txt (key ERR_&lt;ErrorCode&gt;) with an English fallback, and the detail reported by the
        /// failing stage (exception message, ffmpeg output) is appended when available.
        /// </summary>
        public static string GetLocalizedErrorMessage(ErrorCode errorCode, string? detail = null)
        {
            var key = ErrorPrefix + errorCode;
            var message = TextResource.GetText(key);
            if (string.IsNullOrEmpty(message))
            {
                message = fallbackMessages.GetValueOrDefault(key, GenericErrorMessage);
            }
            return string.IsNullOrEmpty(detail) ? message : message + ": " + detail;
        }
    }
}
