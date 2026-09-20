using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using XDM.Core;
using XDM.Core.Clients.Http;
using XDM.Core.Downloader;
using XDM.Core.Downloader.Adaptive;

namespace XDM.Tests
{
    internal sealed class TestCancelRequester : ICancelRequster
    {
        private int transientFailures;

        public ErrorCode Error { get; private set; } = ErrorCode.None;

        public int TransientFailures => transientFailures;

        public void CancelWithFatal(ErrorCode error)
        {
            Error = error;
        }

        public void NotifyTransientFailure()
        {
            Interlocked.Increment(ref transientFailures);
        }

        public void RegisterThread(HttpChunkDownloader chunkDownloader)
        {
        }

        public void UnRegisterThread(HttpChunkDownloader chunkDownloader)
        {
        }

        public void CancelAll()
        {
        }
    }

    [TestFixture]
    public class ChunkDownloadValidationTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "xdm-chunk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Config.LoadConfig(root);
            Config.Instance.MaxRetry = 1;
            Config.Instance.RetryDelay = 1;
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        [Test]
        public void AChunkShorterThanItsRequestedRangeIsNeverMarkedFinished()
        {
            var content = DownloadTestData.Create(512 * 1024);
            //the server answers honestly but with only a quarter of the requested range
            using var server = new TestFileServer(content, maxBytesPerResponse: 64 * 1024);
            server.Start();

            var tempDir = Path.Combine(root, "segments");
            Directory.CreateDirectory(tempDir);
            var segmentFile = Path.Combine(tempDir, "segment");

            var chunk = new Chunk
            {
                Id = "chunk-1",
                Uri = new Uri(server.Url),
                Offset = 0,
                Size = content.Length,
                ChunkState = ChunkState.Ready
            };
            var streamMap = new SimpleStreamMap
            {
                StreamMap = new Dictionary<string, string> { [chunk.Id] = segmentFile }
            };
            var http = HttpClientFactory.NewHttpClient(Config.Instance.Proxy);
            http.Timeout = TimeSpan.FromSeconds(15);

            var requester = new TestCancelRequester();
            var downloader = new HttpChunkDownloader(chunk, http, new Dictionary<string, List<string>>(),
                null, null, streamMap, requester);

            var download = Task.Run(() => downloader.Download());

            //the truncated answer is retried, so the download must keep requesting the missing bytes
            //until the segment really holds chunk.Size bytes
            var reportedFinishedWhileShort = false;
            while (!download.IsCompleted)
            {
                if (chunk.ChunkState == ChunkState.Finished && chunk.Downloaded < chunk.Size)
                {
                    reportedFinishedWhileShort = true;
                }
                Thread.Sleep(1);
            }
            Assert.That(download.IsCompleted, Is.True, "the chunk downloader did not finish");
            Assert.That(download.IsFaulted, Is.False, "the chunk downloader threw: " + download.Exception);

            Assert.That(reportedFinishedWhileShort, Is.False,
                "a truncated chunk was recorded as a finished segment");
            Assert.That(chunk.ChunkState, Is.EqualTo(ChunkState.Finished));
            Assert.That(chunk.Downloaded, Is.EqualTo(chunk.Size),
                "the segment was recorded as finished while it is short");
            Assert.That(server.RequestCount, Is.GreaterThan(1), "the truncated chunk must be re-requested");
            Assert.That(File.ReadAllBytes(segmentFile), Is.EqualTo(content));
        }
    }
}
