using NUnit.Framework;
using Translations;
using XDM.Core;
using XDM.Core.Util;

namespace XDM.Tests
{
    /// <summary>
    /// The assemble and merge phases have no speed and no ETA: the in-progress row renders exactly what
    /// Helpers.GenerateStatusText returns for the row status, so this is what removes the stale
    /// "5.4 MB/s - 00:00:07" that used to sit next to a frozen bar.
    /// </summary>
    [TestFixture]
    public class PhaseStatusTests
    {
        [Test]
        public void AssembleStatusesRenderTheirLabelInsteadOfSpeedAndEta()
        {
            Assert.That(TextResource.GetText("STAT_ASSEMBLING"), Is.EqualTo("Assembling"));
            Assert.That(TextResource.GetText("STAT_MERGING"), Is.EqualTo("Merging audio and video"));

            var item = new InProgressDownloadItem
            {
                Status = DownloadStatus.Assembling,
                DownloadSpeed = "5.4 MB/s",
                ETA = "00:00:07"
            };

            Assert.That(Helpers.GenerateStatusText(item), Is.EqualTo("Assembling"));

            item.Status = DownloadStatus.Merging;
            Assert.That(Helpers.GenerateStatusText(item), Is.EqualTo("Merging audio and video"));

            //speed and ETA are the fallback for a download in progress only
            item.Status = DownloadStatus.Downloading;
            Assert.That(Helpers.GenerateStatusText(item), Is.EqualTo("5.4 MB/s - 00:00:07"));
        }

        [Test]
        public void NewStatusesAreAppendedSoPersistedIntegersKeepTheirMeaning()
        {
            Assert.That((int)DownloadStatus.Downloading, Is.EqualTo(0));
            Assert.That((int)DownloadStatus.Stopped, Is.EqualTo(1));
            Assert.That((int)DownloadStatus.Finished, Is.EqualTo(2));
            Assert.That((int)DownloadStatus.Waiting, Is.EqualTo(3));
            Assert.That((int)DownloadStatus.Assembling, Is.EqualTo(4));
            Assert.That((int)DownloadStatus.Merging, Is.EqualTo(5));
        }
    }
}
