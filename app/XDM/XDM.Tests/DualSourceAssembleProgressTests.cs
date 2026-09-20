using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using XDM.Core;
using XDM.Core.Downloader;
using XDM.Core.Downloader.Progressive;
using XDM.Core.Downloader.Progressive.DualHttp;
using XDM.Core.MediaProcessor;

namespace XDM.Tests
{
    /// <summary>
    /// Stands in for ffmpeg: the merge is the only part of the dual-source assemble that needs a real
    /// muxer, and the band the downloader reports for it is what these tests pin. The fake writes the
    /// concatenation of its two inputs so the test can still verify that both passes were copied.
    /// </summary>
    internal sealed class FakeMediaProcessor : BaseMediaProcessor
    {
        public int MergeCalls { get; private set; }

        public override MediaProcessingResult MergeAudioVideStream(string file1, string file2, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            MergeCalls++;
            foreach (var progress in new[] { 0, 30, 60, 100 })
            {
                UpdateProgress(progress);
            }
            var merged = File.ReadAllBytes(file1).Concat(File.ReadAllBytes(file2)).ToArray();
            File.WriteAllBytes(outfile, merged);
            outFileSize = merged.Length;
            return MediaProcessingResult.Success;
        }

        public override MediaProcessingResult MergeHLSAudioVideStream(string segmentListFile, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            throw new NotSupportedException();
        }

        public override MediaProcessingResult ConvertToMp3Audio(string segmentListFile, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Drives DualSourceHTTPDownloader.AssemblePieces with pre-finished pieces on disk, which is what a
    /// resumed dual-source download looks like right before the two streams are concatenated.
    /// </summary>
    internal sealed class TestDualSourceDownloader : DualSourceHTTPDownloader
    {
        public TestDualSourceDownloader(BaseMediaProcessor mediaProcessor)
            : base(new DualSourceHTTPDownloadInfo
            {
                Uri1 = "http://127.0.0.1:1/primary",
                Uri2 = "http://127.0.0.1:1/secondary"
            }, null, null, null, mediaProcessor)
        {
        }

        private string PieceDir => Path.Combine(Config.Instance.TempDir, Id);

        public void AddPiece(string id, StreamType streamType, byte[] data)
        {
            Directory.CreateDirectory(PieceDir);
            File.WriteAllBytes(Path.Combine(PieceDir, id), data);
            pieces[id] = new Piece
            {
                Id = id,
                Offset = 0,
                Length = data.Length,
                Downloaded = data.Length,
                State = SegmentState.Finished,
                StreamType = streamType
            };
        }

        /// <summary>Makes the file size known, which is the branch that reports a percentage.</summary>
        public void SetKnownSize(long size)
        {
            totalSize = size;
        }

        public bool RunAssemble()
        {
            return AssemblePieces();
        }
    }

    [TestFixture]
    public class DualSourceAssembleProgressTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "xdm-dual-assemble-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Config.LoadConfig(root);
            Config.Instance.MaxRetry = 2;
            Config.Instance.RetryDelay = 1;
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        [Test]
        public void TheConcatAndTheMergeShareOneMonotonicBar()
        {
            var primary = DownloadTestData.Create(2 * 1024 * 1024, 7);
            var secondary = DownloadTestData.Create(1024 * 1024, 11);
            var mediaProcessor = new FakeMediaProcessor();

            var downloader = new TestDualSourceDownloader(mediaProcessor);
            var targetDir = Path.Combine(root, "out");
            downloader.SetTargetDirectory(targetDir);
            downloader.SetFileName("merged.bin", FileNameFetchMode.FileNameAndExtension);
            downloader.AddPiece("primary-piece", StreamType.Primary, primary);
            downloader.AddPiece("secondary-piece", StreamType.Secondary, secondary);
            downloader.SetKnownSize(primary.Length + secondary.Length);

            var events = new List<ProgressResultEventArgs>();
            downloader.AssembingProgressChanged += (s, e) => { lock (events) events.Add(DownloadTestData.Snapshot(e)); };

            Assert.That(downloader.RunAssemble(), Is.True, "the assemble must report a complete copy");
            Assert.That(mediaProcessor.MergeCalls, Is.EqualTo(1));

            var dump = string.Join("; ", events.Select(e => e.Phase + ":" + e.Progress + "/" + e.Downloaded));

            //the phase is announced before the first byte is copied
            Assert.That(events[0].Phase, Is.EqualTo(DownloadPhase.Assembling));
            Assert.That(events[0].Progress, Is.EqualTo(0));
            Assert.That(events[0].Downloaded, Is.EqualTo(0));

            //the bar must never move backwards: the merge used to restart it at 60% after a concat that ran to 100%
            for (var i = 1; i < events.Count; i++)
            {
                Assert.That(events[i].Progress, Is.GreaterThanOrEqualTo(events[i - 1].Progress),
                    "progress went backwards :: " + dump);
            }

            var concat = events.Where(e => e.Phase == DownloadPhase.Assembling).ToList();
            var merge = events.Where(e => e.Phase == DownloadPhase.Merging).ToList();
            Assert.That(concat.Count, Is.GreaterThan(1), "the concat reported no progress :: " + dump);
            Assert.That(merge.Count, Is.GreaterThan(0), "the merge reported no progress :: " + dump);
            Assert.That(concat.All(e => e.Progress <= 50), Is.True, "the concat ran past its half :: " + dump);
            Assert.That(merge.All(e => e.Progress >= 50 && e.Progress <= 99), Is.True,
                "the merge must stay inside 50..99 :: " + dump);

            //the concat denominator is the size of both streams, so it ends exactly at the middle
            Assert.That(concat.Last().Progress, Is.EqualTo(50), "the concat did not reach its half :: " + dump);
            Assert.That(concat.Last().Downloaded, Is.EqualTo((long)(primary.Length + secondary.Length)),
                "the concat byte count must cover both streams :: " + dump);

            var target = downloader.TargetFile;
            Assert.That(target, Is.Not.Null.And.Not.Empty, "the download has no target file");
            Assert.That(File.Exists(target), Is.True, "the assembled file is missing :: " + dump);
            Assert.That(File.ReadAllBytes(target),
                Is.EqualTo(primary.Concat(secondary).ToArray()),
                "the two streams were not copied in order");
        }

        [Test]
        public void AnUnknownSizeConcatStillReportsItsByteCounter()
        {
            var primary = DownloadTestData.Create(512 * 1024, 3);
            var secondary = DownloadTestData.Create(512 * 1024, 5);

            var downloader = new TestDualSourceDownloader(new FakeMediaProcessor());
            downloader.SetTargetDirectory(Path.Combine(root, "out"));
            downloader.SetFileName("unknown.bin", FileNameFetchMode.FileNameAndExtension);
            downloader.AddPiece("primary-piece", StreamType.Primary, primary);
            downloader.AddPiece("secondary-piece", StreamType.Secondary, secondary);
            //no SetKnownSize call: this is the unknown-size download, where no percentage exists

            var events = new List<ProgressResultEventArgs>();
            downloader.AssembingProgressChanged += (s, e) => { lock (events) events.Add(DownloadTestData.Snapshot(e)); };

            Assert.That(downloader.RunAssemble(), Is.True, "the assemble must report a complete copy");

            var dump = string.Join("; ", events.Select(e => e.Phase + ":" + e.Progress + "/" + e.Downloaded));
            var concat = events.Where(e => e.Phase == DownloadPhase.Assembling).ToList();
            Assert.That(concat.Count, Is.GreaterThan(0), "the unknown-size copy reported nothing :: " + dump);
            Assert.That(concat.All(e => e.Progress == 0), Is.True,
                "an unknown total cannot produce a percentage :: " + dump);
            Assert.That(concat.Any(e => e.Downloaded > 0), Is.True,
                "the byte counter must be published even when the total is unknown :: " + dump);
        }
    }
}
