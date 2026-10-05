using System;

namespace XDM.Core.MediaProcessor
{
    /// <summary>
    /// Final result of the TS to MP4 remux step.
    /// </summary>
    public enum RemuxOutcome
    {
        Skipped,
        Converted,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Remux helper skeleton. Methods are not implemented yet; the pipeline is completed by
    /// T007 (ShouldRemux/LooksLikeMpegTs), T009 (Run) and T010 (TryCleanupPendingOutputs).
    /// </summary>
    public static class TsToMp4Remuxer
    {
        public static bool ShouldRemux(bool convertTsToMp4, string targetFileName)
        {
            throw new NotImplementedException("TsToMp4Remuxer.ShouldRemux is not implemented yet (T007).");
        }

        public static bool LooksLikeMpegTs(string path)
        {
            throw new NotImplementedException("TsToMp4Remuxer.LooksLikeMpegTs is not implemented yet (T007).");
        }

        public static RemuxOutcome Run(BaseMediaProcessor mediaProcessor, string tsFile,
            CancelFlag cancel, Action<string, string> checkpointMp4,
            out string finalFile, out long finalSize)
        {
            throw new NotImplementedException("TsToMp4Remuxer.Run is not implemented yet (T009).");
        }

        internal static bool TryCleanupPendingOutputs(string tsPath, string tsStamp,
            string mp4Path, string mp4Stamp, Action<bool> clearPending, out string warning)
        {
            throw new NotImplementedException("TsToMp4Remuxer.TryCleanupPendingOutputs is not implemented yet (T010).");
        }
    }
}
