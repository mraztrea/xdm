using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using XDM.Core;
using XDM.Core.MediaProcessor;

namespace XDM.Tests
{
    [TestFixture]
    public class FFmpegMergeProgressTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "xdm-ffmpeg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Config.LoadConfig(root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        [Test]
        public void MergeReportsIncreasingProgressAndWritesTheOutput()
        {
            if (!FFmpegMediaProcessor.IsFFmpegInstalled())
            {
                Assert.Ignore("ffmpeg is not on PATH");
            }

            var video = Path.Combine(root, "video.mkv");
            var audio = Path.Combine(root, "audio.mkv");
            var output = Path.Combine(root, "merged.mkv");

            RunFFmpeg("-y", "-f", "lavfi", "-i", "testsrc=duration=20:size=320x240:rate=25",
                "-pix_fmt", "yuv420p", "-c:v", "mpeg4", "-q:v", "3", video);
            RunFFmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
                "-c:a", "pcm_s16le", audio);

            //The video is served slowly on purpose: a local merge of the same file finishes in a few
            //milliseconds, which cannot cross the 500 ms progress throttle, so a fast merge legitimately
            //produces a single event and the assertion below would test the disk speed instead.
            using var server = new TestFileServer(File.ReadAllBytes(video), "video.mkv", "video/x-matroska",
                bodyChunkBytes: 16 * 1024, delayPerChunkMs: 50);
            server.Start();

            var processor = new FFmpegMediaProcessor();
            var values = new List<int>();
            processor.ProgressChanged += (s, e) => { lock (values) values.Add(e.Progress); };

            var result = processor.MergeAudioVideStream(server.Url, audio, output, new CancelFlag(),
                out var size);

            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(File.Exists(output), Is.True);
            Assert.That(size, Is.GreaterThan(0));

            lock (values)
            {
                Assert.That(values.Count, Is.GreaterThanOrEqualTo(2), "the merge reported no progress");
                for (var i = 1; i < values.Count; i++)
                {
                    Assert.That(values[i], Is.GreaterThanOrEqualTo(values[i - 1]));
                }
                Assert.That(values.Last(), Is.GreaterThan(values.First()));
                Assert.That(values.All(v => v >= 0 && v <= 99), Is.True,
                    "the merge must stay below 100 until the output file is complete");
            }
        }

        private static void RunFFmpeg(params string[] args)
        {
            var info = new ProcessStartInfo
            {
                FileName = FFmpegMediaProcessor.FindFFmpegBinary(),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info);
            var error = process.StandardError.ReadToEnd();
            Assert.That(process.WaitForExit(120000), Is.True, "ffmpeg timed out");
            Assert.That(process.ExitCode, Is.EqualTo(0), error);
        }
    }
}
