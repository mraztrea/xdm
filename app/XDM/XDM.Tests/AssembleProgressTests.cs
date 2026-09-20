using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using XDM.Core;
using XDM.Core.Downloader;
using XDM.Core.Downloader.Adaptive;
using XDM.Core.Downloader.Adaptive.Hls;
using XDM.Core.Downloader.Progressive.SingleHttp;

namespace XDM.Tests
{
    /// <summary>
    /// File server for the downloader tests: serves one byte array, honouring Range requests, and can
    /// throttle the body or hand out fewer bytes than the requested range.
    /// </summary>
    internal sealed class TestFileServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly byte[] content;
        private readonly int bodyChunkBytes;
        private readonly int delayPerChunkMs;
        private readonly int maxBytesPerResponse;
        private readonly string contentType;
        private int requestCount;

        public int Port { get; }

        public string Url { get; }

        public int RequestCount => requestCount;

        public TestFileServer(byte[] content, string contentFileName = "payload",
            string contentType = "application/octet-stream", int bodyChunkBytes = 64 * 1024,
            int delayPerChunkMs = 0, int maxBytesPerResponse = -1)
        {
            this.content = content;
            this.contentType = contentType;
            this.bodyChunkBytes = bodyChunkBytes;
            this.delayPerChunkMs = delayPerChunkMs;
            this.maxBytesPerResponse = maxBytesPerResponse;
            Port = FindFreePort();
            Url = "http://127.0.0.1:" + Port + "/" + contentFileName;
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
        }

        public void Start()
        {
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true }.Start();
        }

        private void AcceptLoop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    return;
                }
                var captured = ctx;
                ThreadPool.QueueUserWorkItem(_ => Serve(captured));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref requestCount);
            try
            {
                ServeRange(ctx);
            }
            catch (Exception)
            {
                //the downloader aborts requests on purpose; a test server has nothing to report
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void ServeRange(HttpListenerContext ctx)
        {
            var start = 0L;
            var end = (long)content.Length - 1;
            var partial = false;
            var range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
            {
                var spec = range.Substring("bytes=".Length).Split('-');
                start = long.Parse(spec[0]);
                if (spec.Length > 1 && spec[1].Length > 0)
                {
                    end = Math.Min(long.Parse(spec[1]), end);
                }
                partial = true;
            }
            if (start < 0 || start >= content.Length || start > end)
            {
                ctx.Response.StatusCode = 416;
                return;
            }
            var count = (int)(end - start + 1);
            if (maxBytesPerResponse > 0 && count > maxBytesPerResponse)
            {
                //a well formed but truncated answer: fewer bytes than requested, declared honestly
                count = maxBytesPerResponse;
                end = start + count - 1;
            }
            ctx.Response.StatusCode = partial ? 206 : 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = count;
            if (partial)
            {
                ctx.Response.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + content.Length;
            }
            var stream = ctx.Response.OutputStream;
            var written = 0;
            while (written < count)
            {
                var n = Math.Min(bodyChunkBytes, count - written);
                stream.Write(content, (int)(start + written), n);
                written += n;
                if (delayPerChunkMs > 0)
                {
                    Thread.Sleep(delayPerChunkMs);
                }
            }
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    internal static class DownloadTestData
    {
        public static byte[] Create(int length, byte seed = 0)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 31 + seed) % 251);
            }
            return data;
        }

        public static string Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(data));
        }

        public static ProgressResultEventArgs Snapshot(ProgressResultEventArgs args)
        {
            //the downloaders reuse one event args instance, so every notification must be copied
            return new ProgressResultEventArgs
            {
                Progress = args.Progress,
                Downloaded = args.Downloaded,
                DownloadSpeed = args.DownloadSpeed,
                Eta = args.Eta,
                Phase = args.Phase
            };
        }
    }

    /// <summary>
    /// Drives MultiSourceDownloaderBase.Assemble through the real Resume() caller path: the chunks are
    /// pre-finished and the segment files are already on disk, which is what a resumed download looks
    /// like right before the segments are concatenated.
    /// </summary>
    internal sealed class TestMultiSourceDownloader : MultiSourceDownloaderBase
    {
        public TestMultiSourceDownloader() : base(new TestMultiSourceDownloadInfo())
        {
        }

        protected override void SaveState()
        {
        }

        protected override void RestoreState()
        {
        }

        protected override void Init(string tempDir)
        {
        }

        protected override void OnContentTypeReceived(Chunk chunk, string contentType)
        {
        }

        public void SetSegments(string tempDir, bool demuxed, params string[] files)
        {
            _state = new MultiSourceHLSDownloadState
            {
                TempDirectory = tempDir,
                FileSize = -1,
                Demuxed = demuxed
            };
            _chunks = new List<MultiSourceChunk>(files.Length);
            foreach (var file in files)
            {
                var id = Guid.NewGuid().ToString();
                _chunks.Add(new MultiSourceChunk
                {
                    Id = id,
                    Uri = new Uri("http://127.0.0.1:1/" + id),
                    Size = new FileInfo(file).Length,
                    Offset = 0,
                    StreamIndex = 0,
                    ChunkState = ChunkState.Finished
                });
                _chunkStreamMap.StreamMap[id] = file;
            }
        }

        public bool RunAssemble()
        {
            return Assemble();
        }

        public int ChunkCount => _chunks == null ? -1 : _chunks.Count;

        public long TargetLength()
        {
            var file = TargetFile;
            try
            {
                return file != null && File.Exists(file) ? new FileInfo(file).Length : -1;
            }
            catch (IOException)
            {
                //the downloader may delete the abandoned target while this diagnostic runs
                return -1;
            }
        }
    }

    internal sealed class TestMultiSourceDownloadInfo : MultiSourceDownloadInfo
    {
    }

    [TestFixture]
    public class ProgressiveAssembleProgressTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "xdm-assemble-" + Guid.NewGuid().ToString("N"));
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
        public void DownloadPublishes100BeforeTheAssembleAndWritesTheExactFile()
        {
            var source = DownloadTestData.Create(12 * 1024 * 1024);
            using var server = new TestFileServer(source);
            server.Start();

            var targetDir = Path.Combine(root, "target");
            Directory.CreateDirectory(targetDir);

            var downloader = new SingleSourceHTTPDownloader(new SingleSourceHTTPDownloadInfo
            {
                Uri = server.Url,
                File = "payload.bin"
            });
            downloader.SetTargetDirectory(targetDir);
            downloader.SetFileName("payload.bin", FileNameFetchMode.FileNameAndExtension);

            var events = new List<ProgressResultEventArgs>();
            var done = new ManualResetEventSlim(false);
            var error = ErrorCode.None;
            downloader.ProgressChanged += (s, e) => { lock (events) events.Add(DownloadTestData.Snapshot(e)); };
            downloader.AssembingProgressChanged += (s, e) => { lock (events) events.Add(DownloadTestData.Snapshot(e)); };
            downloader.Finished += (s, e) => done.Set();
            downloader.Cancelled += (s, e) => done.Set();
            downloader.Failed += (s, e) => { error = e.ErrorCode; done.Set(); };

            downloader.Start();
            Assert.That(done.Wait(TimeSpan.FromSeconds(60)), Is.True, "the download never finished");
            Assert.That(error, Is.EqualTo(ErrorCode.None), "the download failed");

            List<ProgressResultEventArgs> reported;
            lock (events) reported = events.ToList();

            var lastDownloadSample = reported.FindLastIndex(
                e => e.Phase == DownloadPhase.Downloading && e.Progress == 100);
            var firstAssembleEvent = reported.FindIndex(e => e.Phase == DownloadPhase.Assembling);

            Assert.That(lastDownloadSample, Is.GreaterThanOrEqualTo(0),
                "no download phase sample reached 100 before the assemble");
            Assert.That(firstAssembleEvent, Is.GreaterThan(lastDownloadSample),
                "the assemble must start after the final download sample");

            var dump = string.Join("; ", reported.Select(e => e.Phase + ":" + e.Progress + "/" + e.Downloaded));
            var assembleEvents = reported.Where(e => e.Phase == DownloadPhase.Assembling).ToList();
            Assert.That(assembleEvents.Count, Is.GreaterThan(0), "the assemble reported no progress :: " + dump);
            Assert.That(assembleEvents[0].Progress, Is.EqualTo(0),
                "the assemble must announce its phase before the first byte :: " + dump);
            Assert.That(assembleEvents[0].Downloaded, Is.EqualTo(0),
                "the phase announcement carries no bytes yet :: " + dump);
            for (var i = 1; i < assembleEvents.Count; i++)
            {
                Assert.That(assembleEvents[i].Progress, Is.GreaterThanOrEqualTo(assembleEvents[i - 1].Progress),
                    "progress went backwards :: " + dump);
            }
            Assert.That(assembleEvents.Last().Progress, Is.EqualTo(100), "the assemble never completed :: " + dump);
            Assert.That(assembleEvents.Last().Downloaded, Is.EqualTo((long)source.Length),
                "the assemble byte count is wrong :: " + dump);

            //the downloader picks the final name from the response, so ask it where it wrote the file
            var target = downloader.TargetFile;
            Assert.That(target, Is.Not.Null.And.Not.Empty, "the download has no target file");
            Assert.That(File.Exists(target), Is.True, "the assembled file is missing :: " + dump);
            Assert.That(DownloadTestData.Sha256(File.ReadAllBytes(target)),
                Is.EqualTo(DownloadTestData.Sha256(source)), "the assembled file differs from the source");
        }
    }

    [TestFixture]
    public class AdaptiveAssembleProgressTests
    {
        private string root;
        private readonly List<string> segmentFiles = new List<string>();

        [SetUp]
        public void SetUp()
        {
            segmentFiles.Clear();
            root = Path.Combine(Path.GetTempPath(), "xdm-concat-" + Guid.NewGuid().ToString("N"));
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

        private byte[][] WriteSegments(int count, int segmentLength)
        {
            var segmentDir = Path.Combine(root, "segments");
            Directory.CreateDirectory(segmentDir);
            segmentFiles.Clear();
            var segments = new byte[count][];
            for (var i = 0; i < count; i++)
            {
                segments[i] = DownloadTestData.Create(segmentLength, (byte)(i + 1));
                var file = Path.Combine(segmentDir, "seg" + i + ".bin");
                File.WriteAllBytes(file, segments[i]);
                segmentFiles.Add(file);
            }
            return segments;
        }

        [Test]
        public void ConcatPublishesIncreasingProgressAndWritesTheExactFile()
        {
            var segments = WriteSegments(4, 4 * 1024 * 1024);
            var totalBytes = segments.Sum(s => (long)s.Length);
            var segmentDir = Path.GetDirectoryName(segmentFiles[0]);

            var downloader = new TestMultiSourceDownloader();
            downloader.SetSegments(segmentDir, false, segmentFiles.ToArray());
            var targetDir = Path.Combine(root, "out");
            downloader.SetTargetDirectory(targetDir);
            downloader.SetFileName("merged.bin", FileNameFetchMode.FileNameAndExtension);

            var events = new List<ProgressResultEventArgs>();
            downloader.AssembingProgressChanged += (s, e) => { lock (events) events.Add(DownloadTestData.Snapshot(e)); };

            Assert.That(downloader.RunAssemble(), Is.True, "the assemble must report a complete copy");

            Assert.That(events.Count, Is.GreaterThan(1), "the copy reported no progress");
            Assert.That(events[0].Phase, Is.EqualTo(DownloadPhase.Assembling));
            Assert.That(events[0].Progress, Is.EqualTo(0));
            for (var i = 1; i < events.Count; i++)
            {
                Assert.That(events[i].Phase, Is.EqualTo(DownloadPhase.Assembling));
                Assert.That(events[i].Progress, Is.GreaterThanOrEqualTo(events[i - 1].Progress));
            }
            Assert.That(events.Last().Progress, Is.EqualTo(100));
            Assert.That(events.Last().Downloaded, Is.EqualTo(totalBytes));
            Assert.That(events.Any(e => e.Downloaded > 0), Is.True, "the progress carried no byte count");

            var target = Path.Combine(targetDir, "merged.bin");
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(segments.SelectMany(s => s).ToArray()));
            Assert.That(downloader.FileSize, Is.EqualTo(totalBytes));
            Assert.That(Directory.Exists(segmentDir), Is.False,
                "the segment files are deleted once the copy is complete");
        }

        [Test]
        public void CancelDuringTheConcatIsReportedOnceAndRemovesThePartialTarget()
        {
            WriteSegments(4, 8 * 1024 * 1024);
            var segmentDir = Path.GetDirectoryName(segmentFiles[0]);

            var downloader = new TestMultiSourceDownloader();
            downloader.SetSegments(segmentDir, false, segmentFiles.ToArray());
            var targetDir = Path.Combine(root, "out");
            downloader.SetTargetDirectory(targetDir);
            downloader.SetFileName("partial.bin", FileNameFetchMode.FileNameAndExtension);

            var cancelled = 0;
            var finished = 0;
            var done = new ManualResetEventSlim(false);
            var reported = new List<ProgressResultEventArgs>();

            downloader.AssembingProgressChanged += (s, e) =>
            {
                lock (reported) reported.Add(DownloadTestData.Snapshot(e));
                //stop as soon as the copy has started writing
                if (e.Downloaded > 0) downloader.Stop();
            };
            downloader.Cancelled += (s, e) => { Interlocked.Increment(ref cancelled); done.Set(); };
            downloader.Finished += (s, e) => { Interlocked.Increment(ref finished); done.Set(); };

            downloader.Resume();

            var reportedCancellation = done.Wait(TimeSpan.FromSeconds(30));
            string dump;
            lock (reported)
            {
                dump = string.Join("; ", reported.Select(e => e.Phase + ":" + e.Progress + "/" + e.Downloaded));
            }
            dump += " | chunks=" + downloader.ChunkCount + " targetLength=" + downloader.TargetLength();
            Assert.That(reportedCancellation, Is.True, "the cancellation was never reported :: " + dump);
            Assert.That(finished, Is.EqualTo(0), "an abandoned assemble must not be reported as finished :: " + dump);
            Assert.That(cancelled, Is.EqualTo(1),
                "the cancellation must be reported exactly once (cancelled=" + cancelled + ") :: " + dump);
            Assert.That(File.Exists(Path.Combine(targetDir, "partial.bin")), Is.False,
                "the truncated target file must be deleted");
            Assert.That(File.Exists(segmentFiles[0]), Is.True,
                "the segment files must survive so the download stays resumable");
        }
    }
}
