using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using XDM.Core;
using XDM.Core.Downloader.Adaptive.Hls;
using XDM.Core.Downloader.Progressive.SingleHttp;
using XDM.Core.IO;
using XDM.Core.MediaProcessor;

namespace XDM.Tests
{
    /// <summary>
    /// Kiểm thử hành vi cho FR-003/FR-004/FR-010 (TS->MP4 remux) và round-trip checkpoint state
    /// (T004/D8/D16). Viết ở T005, trước khi T006-T010 triển khai logic: các ca chưa được hỗ trợ
    /// được mong đợi đỏ cho tới lúc đó.
    /// </summary>
    [TestFixture]
    [NonParallelizable] // bảo vệ singleton Config dùng chung trong tiến trình test
    public class TsToMp4RemuxerTests
    {
        private static readonly byte[] EndMarker = { (byte)'E', (byte)'N', (byte)'D', (byte)'.' };

        // Trường private của singleton Config; đọc/ghi qua reflection để không thêm API vào Config
        // và không kích hoạt Config.Instance (tránh tạo profile thật).
        private static readonly FieldInfo ConfigInstanceField =
            typeof(Config).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!;

        private string tempDir;
        private string previousDataDir;
        private string previousAppDir;
        private object? previousInstance;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "xdm-ts-remux-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // Chụp toàn bộ global Config sẽ bị đổi; không gọi Config.Instance ở đây.
            previousDataDir = Config.DataDir;
            previousAppDir = Config.AppDir;
            previousInstance = ConfigInstanceField.GetValue(null);

            // Profile QA tạm; không đụng ~/.xdm-app-data thật.
            Config.LoadConfig(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            // Khôi phục global Config trước khi xoá thư mục tạm để instance không trỏ vào thư mục đã xoá.
            Config.DataDir = previousDataDir;
            Config.AppDir = previousAppDir;
            ConfigInstanceField.SetValue(null, previousInstance);

            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // dọn dẹp best effort
            }
        }

        // ------------------------------------------------------------------ ShouldRemux

        [Test]
        public void ShouldRemux_TrueFlagAndTsExtension_ReturnsTrue()
        {
            Assert.That(TsToMp4Remuxer.ShouldRemux(true, "clip.ts"), Is.True);
        }

        [Test]
        public void ShouldRemux_IsCaseInsensitiveForExtension()
        {
            Assert.That(TsToMp4Remuxer.ShouldRemux(true, "clip.TS"), Is.True);
        }

        [Test]
        public void ShouldRemux_NonTsExtension_ReturnsFalse()
        {
            Assert.That(TsToMp4Remuxer.ShouldRemux(true, "clip.mp4"), Is.False);
        }

        [Test]
        public void ShouldRemux_FalseFlag_ReturnsFalse()
        {
            Assert.That(TsToMp4Remuxer.ShouldRemux(false, "clip.ts"), Is.False);
        }

        // ------------------------------------------------------------------ LooksLikeMpegTs

        [Test]
        public void LooksLikeMpegTs_FiveSyncBytesAtArbitraryOffset_ReturnsTrue()
        {
            var buf = new byte[4096];
            var offset = 7;
            for (var i = 0; i < 5; i++)
            {
                buf[offset + i * 188] = 0x47;
            }

            Assert.That(TsToMp4Remuxer.LooksLikeMpegTs(WriteFixture("prefixed.ts", buf)), Is.True);
        }

        [Test]
        public void LooksLikeMpegTs_FourSyncBytesOnly_ReturnsFalse()
        {
            // Detector yêu cầu ít nhất 5 sync byte; bốn byte không đủ để bật hint forced.
            var buf = new byte[4096];
            var offset = 3;
            for (var i = 0; i < 4; i++)
            {
                buf[offset + i * 188] = 0x47;
            }

            Assert.That(TsToMp4Remuxer.LooksLikeMpegTs(WriteFixture("four-sync.ts", buf)), Is.False);
        }

        [Test]
        public void LooksLikeMpegTs_NoSyncPattern_ReturnsFalse()
        {
            var buf = new byte[4096];
            Assert.That(TsToMp4Remuxer.LooksLikeMpegTs(WriteFixture("plain.ts", buf)), Is.False);
        }

        [Test]
        public void LooksLikeMpegTs_SignatureBeyondFirstMiB_ReturnsFalse()
        {
            // Detector chỉ quét tối đa 1 MiB đầu; hint false tuyệt đối không được bỏ qua remux.
            var buf = new byte[(1 << 20) + 4096];
            var offset = (1 << 20) + 100;
            for (var i = 0; i < 5; i++)
            {
                buf[offset + i * 188] = 0x47;
            }

            Assert.That(TsToMp4Remuxer.LooksLikeMpegTs(WriteFixture("late.ts", buf)), Is.False);
        }

        // ------------------------------------------------------------------ Probe output parser

        [Test]
        public void ProbeOutputConfirmsTsVideo_MpegTsWithKnownVideoCodecAndResolution_ReturnsTrue()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Duration: 00:00:02.00, start: 1.400000, bitrate: 435 kb/s",
                "  Stream #0:0[0x100]: Video: mpeg2video (Main), yuv420p, 160x90, 25 fps, 90k tbn",
                "  Stream #0:1[0x101]: Audio: ac3, 48000 Hz, stereo, fltp, 192 kb/s"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.True);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_MpegTsWithH264_ReturnsTrue()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: h264 (High), yuv420p, 1920x1080, 25 fps"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.True);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_AutoProbeMisdetect_RequiresForcedFallback()
        {
            // Auto-probe file TS có prefix có thể nhận nhầm container; output đó không được xác nhận
            // TS video, nên caller vẫn phải chạy forced MPEGTS probe.
            var misdetected = new[]
            {
                "Input #0, mpeg, from 'clip.ts':",
                "  Stream #0:0[0x1c0]: Audio: mp2, 48000 Hz, stereo, s16p, 192 kb/s"
            };

            Assert.That(ConfirmsTsVideo(misdetected), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_AutoProbeError_RequiresForcedFallback()
        {
            var noMetadata = new[] { "clip.ts: Invalid data found when processing input" };
            Assert.That(ConfirmsTsVideo(noMetadata), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_ForcedHeaderWithoutVideoStream_ReturnsFalse()
        {
            // Demux "-f mpegts" ép có thể in header cho dữ liệu rác; chỉ header là chưa đủ.
            var headerOnly = new[]
            {
                "Input #0, mpegts, from 'junk.ts':",
                "  Duration: N/A, bitrate: N/A"
            };

            Assert.That(ConfirmsTsVideo(headerOnly), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_VideoCodecNone_ReturnsFalse()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: none, 0x0"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_VideoCodecUnknown_ReturnsFalse()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: unknown, 640x480"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_ZeroResolution_ReturnsFalse()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: h264, yuv420p, 0x0"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_ZeroWidth_ReturnsFalse()
        {
            // Cả hai chiều phải dương: width 0 với height hợp lệ vẫn không xác nhận.
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: h264, yuv420p, 0x720"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.False);
        }

        [Test]
        public void ProbeOutputConfirmsTsVideo_ZeroHeight_ReturnsFalse()
        {
            var lines = new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: h264, yuv420p, 1280x0"
            };

            Assert.That(ConfirmsTsVideo(lines), Is.False);
        }

        // ------------------------------------------------------------------ FFmpeg args

        [Test]
        public void CreateRemuxArgs_StreamCopyOnly()
        {
            var args = CreateRemuxArgs("/in/movie.ts", "/out/movie.mp4");

            Assert.That(args, Is.EqualTo(new[]
            {
                "-hide_banner", "-nostdin", "-f", "mpegts", "-i", "/in/movie.ts",
                "-map", "0:v?", "-map", "0:a?", "-c", "copy", "/out/movie.mp4", "-y"
            }));
            // FR-004: không bao giờ mã hoá lại bất kỳ stream nào.
            Assert.That(args, Has.No.Member("libx264"));
            Assert.That(args, Has.No.Member("-crf"));
            Assert.That(args, Does.Contain("copy"));
        }

        [Test]
        public void CreateProbeTsArgs_AutoProbeHasNoForcedDemuxer()
        {
            var args = CreateProbeTsArgs("/in/movie.ts", false);

            Assert.That(args, Is.EqualTo(new[] { "-hide_banner", "-nostdin", "-i", "/in/movie.ts" }));
        }

        [Test]
        public void CreateProbeTsArgs_ForcedProbeUsesMpegTsDemuxer()
        {
            var args = CreateProbeTsArgs("/in/movie.ts", true);

            Assert.That(args, Is.EqualTo(new[] { "-hide_banner", "-nostdin", "-f", "mpegts", "-i", "/in/movie.ts" }));
        }

        // ------------------------------------------------------------------ ProbeTs orchestration (fake runner)

        [Test]
        public void ProbeTs_AutoProbeFails_RunsForcedFallback()
        {
            var processor = new FakeProbeProcessor(
                (1, new[] { "clip.ts: Invalid data found when processing input" }),
                (1, MpegTsVideoLines()));

            var result = processor.ProbeTs("/in/clip.ts", false, new CancelFlag(), out var isTsVideo);

            Assert.That(processor.ProbeArgs.Count, Is.EqualTo(2), "auto-probe lỗi phải được thử lại bằng forced");
            Assert.That(processor.ProbeArgs[0], Has.No.Member("mpegts"));
            Assert.That(processor.ProbeArgs[1], Has.Member("mpegts"));
            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(isTsVideo, Is.True);
        }

        [Test]
        public void ProbeTs_AutoProbeMisdetects_RunsForcedFallback()
        {
            var processor = new FakeProbeProcessor(
                (0, new[] { "Input #0, mpeg, from 'clip.ts':", "  Stream #0:0[0x1c0]: Audio: mp2, 48000 Hz" }),
                (1, MpegTsVideoLines()));

            var result = processor.ProbeTs("/in/clip.ts", false, new CancelFlag(), out var isTsVideo);

            Assert.That(processor.ProbeArgs.Count, Is.EqualTo(2), "auto nhận định dạng khác phải được thử lại bằng forced");
            Assert.That(processor.ProbeArgs[0], Has.No.Member("mpegts"));
            Assert.That(processor.ProbeArgs[1], Has.Member("mpegts"));
            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(isTsVideo, Is.True);
        }

        [Test]
        public void ProbeTs_AutoProbeConfirms_DoesNotFallBack()
        {
            var processor = new FakeProbeProcessor((1, MpegTsVideoLines()));

            var result = processor.ProbeTs("/in/clip.ts", false, new CancelFlag(), out var isTsVideo);

            Assert.That(processor.ProbeArgs.Count, Is.EqualTo(1), "đã xác nhận video TS thì không chạy forced");
            Assert.That(processor.ProbeArgs[0], Has.No.Member("mpegts"));
            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(isTsVideo, Is.True);
        }

        [Test]
        public void ProbeTs_HasTsSignature_RunsForcedOnly()
        {
            var processor = new FakeProbeProcessor((1, MpegTsVideoLines()));

            var result = processor.ProbeTs("/in/clip.ts", true, new CancelFlag(), out var isTsVideo);

            Assert.That(processor.ProbeArgs.Count, Is.EqualTo(1), "hint true chỉ chạy forced probe");
            Assert.That(processor.ProbeArgs[0], Has.Member("mpegts"));
            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(isTsVideo, Is.True);
        }

        [Test]
        public void ProbeTs_CancelledAfterAuto_DoesNotRunFallback()
        {
            var processor = new FakeProbeProcessor(
                (1, new[] { "clip.ts: Invalid data found when processing input" }),
                (1, MpegTsVideoLines()));
            processor.OnProbe = flag => flag.Cancel();

            processor.ProbeTs("/in/clip.ts", false, new CancelFlag(), out _);

            Assert.That(processor.ProbeArgs.Count, Is.EqualTo(1), "huỷ sau auto thì không chạy lượt fallback");
        }

        [Test]
        public void ProbeTs_ExitOneWithValidTsVideoMetadata_ReturnsSuccess()
        {
            // Exit 1 do probe không có output không tự là lỗi khi metadata TS video hợp lệ.
            var processor = new FakeProbeProcessor((1, MpegTsVideoLines()));

            var result = processor.ProbeTs("/in/clip.ts", false, new CancelFlag(), out var isTsVideo);

            Assert.That(result, Is.EqualTo(MediaProcessingResult.Success));
            Assert.That(isTsVideo, Is.True);
        }

        // ------------------------------------------------------------------ Run (fake processor)

        [Test]
        public void Run_SmallTextWithoutHint_SkipsWithoutCallingProcessor()
        {
            var tsFile = WriteFixture("readme.ts", Encoding.UTF8.GetBytes("const x: number = 1;\n"));
            var processor = new FakeMediaProcessor();

            var outcome = TsToMp4Remuxer.Run(processor, tsFile, CancelFlag.None,
                (_, _) => { }, out var finalFile, out var finalSize);

            Assert.That(outcome, Is.EqualTo(RemuxOutcome.Skipped));
            Assert.That(finalFile, Is.EqualTo(tsFile));
            Assert.That(finalSize, Is.EqualTo(new FileInfo(tsFile).Length));
            Assert.That(processor.ProbeCalls, Is.EqualTo(0), "không gọi FFmpeg probe cho file chắc chắn không phải TS");
        }

        [Test]
        public void Run_ProbeReportsNoTsVideo_SkipsAndKeepsTs()
        {
            var tsFile = WriteFixture("hinted.ts", TsSyncFixture());
            var processor = new FakeMediaProcessor
            {
                ProbeResult = MediaProcessingResult.Success,
                ProbeIsTsVideo = false
            };

            var outcome = TsToMp4Remuxer.Run(processor, tsFile, CancelFlag.None,
                (_, _) => { }, out var finalFile, out var finalSize);

            Assert.That(outcome, Is.EqualTo(RemuxOutcome.Skipped));
            Assert.That(finalFile, Is.EqualTo(tsFile));
            Assert.That(File.Exists(tsFile), Is.True);
            Assert.That(processor.RemuxCalls, Is.EqualTo(0));
        }

        [Test]
        public void Run_RemuxSuccess_ReturnsConvertedAndRemovesTs()
        {
            var tsFile = WriteFixture("movie.ts", TsSyncFixture());
            var checkpoints = new List<(string Path, string Stamp)>();
            var processor = new FakeMediaProcessor
            {
                ProbeResult = MediaProcessingResult.Success,
                ProbeIsTsVideo = true,
                RemuxResult = MediaProcessingResult.Success,
                OnRemux = outfile => File.WriteAllBytes(outfile, new byte[2048])
            };

            var outcome = TsToMp4Remuxer.Run(processor, tsFile, CancelFlag.None,
                (path, stamp) => checkpoints.Add((path, stamp)), out var finalFile, out var finalSize);

            Assert.That(outcome, Is.EqualTo(RemuxOutcome.Converted));
            Assert.That(finalFile, Does.EndWith(".mp4"));
            Assert.That(File.Exists(finalFile), Is.True);
            Assert.That(finalSize, Is.EqualTo(2048));
            Assert.That(File.Exists(tsFile), Is.False, "chỉ xoá TS sau khi remux thành công");
            Assert.That(checkpoints, Is.Not.Empty, "checkpointMp4 phải được gọi cho MP4 đã reserve");
            Assert.That(checkpoints.Last().Path, Is.EqualTo(finalFile));
            Assert.That(checkpoints.Last().Stamp, Is.Not.Empty, "stamp được chốt sau khi process đóng");
        }

        [Test]
        public void Run_RemuxFailure_KeepsTsAndRemovesPartialMp4()
        {
            var tsFile = WriteFixture("broken.ts", TsSyncFixture());
            string? partialMp4 = null;
            var processor = new FakeMediaProcessor
            {
                ProbeResult = MediaProcessingResult.Success,
                ProbeIsTsVideo = true,
                RemuxResult = MediaProcessingResult.Failed,
                RemuxError = "ffmpeg exited with code 1",
                OnRemux = outfile =>
                {
                    partialMp4 = outfile;
                    File.WriteAllBytes(outfile, new byte[10]);
                }
            };

            var outcome = TsToMp4Remuxer.Run(processor, tsFile, CancelFlag.None,
                (_, _) => { }, out var finalFile, out var finalSize);

            Assert.That(outcome, Is.EqualTo(RemuxOutcome.Failed));
            Assert.That(finalFile, Is.EqualTo(tsFile));
            Assert.That(finalSize, Is.EqualTo(new FileInfo(tsFile).Length));
            Assert.That(File.Exists(tsFile), Is.True, "file TS nguồn phải sống sót khi remux lỗi");
            Assert.That(partialMp4, Is.Not.Null);
            Assert.That(File.Exists(partialMp4!), Is.False, "MP4 dở dang từ lượt lỗi phải bị xoá");
        }

        // ------------------------------------------------------------------ State round-trip

        [TestCase(true)]
        [TestCase(false)]
        public void SingleSource_FlagAndCheckpointRoundTrip(bool convert)
        {
            var id = "roundtrip-single-" + convert;
            var state = new SingleSourceHTTPDownloaderState
            {
                Id = id,
                TempDir = Path.Combine(tempDir, "temp"),
                FileSize = 4096,
                Url = new Uri("http://host/movie.ts"),
                ConvertTsToMp4 = convert,
                PendingRemuxTsPath = convert ? "/dest/movie.ts" : string.Empty,
                PendingRemuxTsStamp = convert ? "4096:638000000000000000:638000000000000001" : string.Empty,
                PendingRemuxMp4Path = convert ? "/dest/movie.mp4" : string.Empty,
                PendingRemuxMp4Stamp = convert ? "2048:638000000000000002:638000000000000003" : string.Empty
            };

            DownloadStateIO.Save(state);
            var loaded = DownloadStateIO.LoadSingleSourceHTTPDownloaderState(id);

            Assert.That(loaded.ConvertTsToMp4, Is.EqualTo(convert));
            Assert.That(loaded.PendingRemuxTsPath, Is.EqualTo(state.PendingRemuxTsPath));
            Assert.That(loaded.PendingRemuxTsStamp, Is.EqualTo(state.PendingRemuxTsStamp));
            Assert.That(loaded.PendingRemuxMp4Path, Is.EqualTo(state.PendingRemuxMp4Path));
            Assert.That(loaded.PendingRemuxMp4Stamp, Is.EqualTo(state.PendingRemuxMp4Stamp));
        }

        [Test]
        public void SingleSource_LegacyStateWithoutCheckpoint_DefaultsToNoConvertAndEmptyCheckpoint()
        {
            var id = "legacy-single";
            WriteTransactedState(id + ".state", LegacySingleSourcePayload(id, Path.Combine(tempDir, "temp"), new Uri("http://host/movie.ts")));

            var state = DownloadStateIO.LoadSingleSourceHTTPDownloaderState(id);

            Assert.That(state.ConvertTsToMp4, Is.False, "bản ghi .state cũ phải mặc định cờ false");
            Assert.That(state.PendingRemuxTsPath, Is.Empty);
            Assert.That(state.PendingRemuxTsStamp, Is.Empty);
            Assert.That(state.PendingRemuxMp4Path, Is.Empty);
            Assert.That(state.PendingRemuxMp4Stamp, Is.Empty);
        }

        [Test]
        public void SingleSource_TruncatedCheckpointGroup_ThrowsInsteadOfSilentlyClearing()
        {
            // State mang checkpoint dở dang phải fail closed, không được đọc thành "rỗng".
            var id = "truncated-single";
            var payload = LegacySingleSourcePayload(id, Path.Combine(tempDir, "temp"), new Uri("http://host/movie.ts"));
            var withCheckpoint = AppendTruncatedCheckpoint(payload, true, "/dest/movie.ts");

            WriteTransactedState(id + ".state", withCheckpoint);

            Assert.That(() => DownloadStateIO.LoadSingleSourceHTTPDownloaderState(id), Throws.Exception);
        }

        [Test]
        public void SingleSource_StateWithFlagButNoCheckpoint_KeepsFlagAndEmptyCheckpoint()
        {
            var id = "flag-only-single";
            var payload = LegacySingleSourcePayload(id, Path.Combine(tempDir, "temp"), new Uri("http://host/movie.ts"));
            var withFlag = AppendFlagOnly(payload, true);

            WriteTransactedState(id + ".state", withFlag);

            var state = DownloadStateIO.LoadSingleSourceHTTPDownloaderState(id);

            Assert.That(state.ConvertTsToMp4, Is.True);
            Assert.That(state.PendingRemuxTsPath, Is.Empty);
            Assert.That(state.PendingRemuxTsStamp, Is.Empty);
            Assert.That(state.PendingRemuxMp4Path, Is.Empty);
            Assert.That(state.PendingRemuxMp4Stamp, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Hls_FlagAndCheckpointRoundTrip(bool convert)
        {
            var id = "roundtrip-hls-" + convert;
            var state = new MultiSourceHLSDownloadState
            {
                Id = id,
                TempDirectory = Path.Combine(tempDir, "hls"),
                FileSize = 8192,
                ConvertTsToMp4 = convert,
                PendingRemuxTsPath = convert ? "/dest/stream.ts" : string.Empty,
                PendingRemuxTsStamp = convert ? "8192:638000000000000010:638000000000000011" : string.Empty,
                PendingRemuxMp4Path = convert ? "/dest/stream.mp4" : string.Empty,
                PendingRemuxMp4Stamp = convert ? "4096:638000000000000012:638000000000000013" : string.Empty
            };

            DownloadStateIO.Save(state);
            var loaded = DownloadStateIO.LoadMultiSourceHLSDownloadState(id);

            Assert.That(loaded.ConvertTsToMp4, Is.EqualTo(convert));
            Assert.That(loaded.PendingRemuxTsPath, Is.EqualTo(state.PendingRemuxTsPath));
            Assert.That(loaded.PendingRemuxTsStamp, Is.EqualTo(state.PendingRemuxTsStamp));
            Assert.That(loaded.PendingRemuxMp4Path, Is.EqualTo(state.PendingRemuxMp4Path));
            Assert.That(loaded.PendingRemuxMp4Stamp, Is.EqualTo(state.PendingRemuxMp4Stamp));
        }

        [Test]
        public void Hls_LegacyStateWithoutCheckpoint_DefaultsToNoConvertAndEmptyCheckpoint()
        {
            var id = "legacy-hls";
            WriteTransactedState(id + ".state", LegacyHlsPayload(id, Path.Combine(tempDir, "hls")));

            var state = DownloadStateIO.LoadMultiSourceHLSDownloadState(id);

            Assert.That(state.ConvertTsToMp4, Is.False);
            Assert.That(state.PendingRemuxTsPath, Is.Empty);
            Assert.That(state.PendingRemuxTsStamp, Is.Empty);
            Assert.That(state.PendingRemuxMp4Path, Is.Empty);
            Assert.That(state.PendingRemuxMp4Stamp, Is.Empty);
        }

        [Test]
        public void Hls_StateWithFlagButNoCheckpoint_KeepsFlagAndEmptyCheckpoint()
        {
            // EOF ngay sau cờ (không có bytes checkpoint) -> giữ cờ, checkpoint rỗng.
            var id = "flag-only-hls";
            var payload = LegacyHlsPayload(id, Path.Combine(tempDir, "hls"));
            var withFlag = AppendFlagOnly(payload, true);

            WriteTransactedState(id + ".state", withFlag);

            var state = DownloadStateIO.LoadMultiSourceHLSDownloadState(id);

            Assert.That(state.ConvertTsToMp4, Is.True);
            Assert.That(state.PendingRemuxTsPath, Is.Empty);
            Assert.That(state.PendingRemuxTsStamp, Is.Empty);
            Assert.That(state.PendingRemuxMp4Path, Is.Empty);
            Assert.That(state.PendingRemuxMp4Stamp, Is.Empty);
        }

        [Test]
        public void Hls_TruncatedCheckpointGroup_ThrowsInsteadOfSilentlyClearing()
        {
            var id = "truncated-hls";
            var payload = LegacyHlsPayload(id, Path.Combine(tempDir, "hls"));
            var withCheckpoint = AppendTruncatedCheckpoint(payload, true, "/dest/stream.ts");

            WriteTransactedState(id + ".state", withCheckpoint);

            Assert.That(() => DownloadStateIO.LoadMultiSourceHLSDownloadState(id), Throws.Exception);
        }

        // ------------------------------------------------------------------ helpers

        private static bool ConfirmsTsVideo(IEnumerable<string> stderrLines)
        {
            var method = typeof(FFmpegMediaProcessor).GetMethod("ProbeOutputConfirmsTsVideo",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IEnumerable<string>) }, null);
            Assert.That(method, Is.Not.Null, "không tìm thấy seam nội bộ FFmpegMediaProcessor.ProbeOutputConfirmsTsVideo");
            return (bool)method!.Invoke(null, new object[] { stderrLines })!;
        }

        private static string[] CreateRemuxArgs(string infile, string outfile)
        {
            return (string[])InvokeSeam("CreateRemuxArgs", new[] { typeof(string), typeof(string) }, infile, outfile)!;
        }

        private static string[] CreateProbeTsArgs(string infile, bool forceMpegTs)
        {
            return (string[])InvokeSeam("CreateProbeTsArgs", new[] { typeof(string), typeof(bool) }, infile, forceMpegTs)!;
        }

        private static object? InvokeSeam(string name, Type[] parameterTypes, params object[] args)
        {
            var method = typeof(FFmpegMediaProcessor).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Static, null, parameterTypes, null);
            Assert.That(method, Is.Not.Null, $"không tìm thấy seam nội bộ FFmpegMediaProcessor.{name}");
            return method!.Invoke(null, args);
        }

        private static string[] MpegTsVideoLines()
        {
            return new[]
            {
                "Input #0, mpegts, from 'clip.ts':",
                "  Stream #0:0[0x100]: Video: mpeg2video (Main), yuv420p, 160x90, 25 fps",
                "  Stream #0:1[0x101]: Audio: ac3, 48000 Hz, stereo, fltp, 192 kb/s"
            };
        }

        private string WriteFixture(string name, byte[] bytes)
        {
            var path = Path.Combine(tempDir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static byte[] TsSyncFixture()
        {
            var buf = new byte[4096];
            for (var i = 0; i < 5; i++)
            {
                buf[3 + i * 188] = 0x47;
            }
            return buf;
        }

        /// <summary>Payload single-source cũ: thiếu ConvertTsToMp4 và các field checkpoint.</summary>
        private static byte[] LegacySingleSourcePayload(string id, string tempDir, Uri url)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(id);
            w.Write(tempDir ?? string.Empty);
            w.Write(4096L);
            w.Write(DateTime.UtcNow.ToBinary());
            w.Write(0);
            w.Write(url.ToString());
            w.Write(false); // hasHeaders
            w.Write(false); // hasCookies
            w.Write(false); // hasProxy
            w.Write(false); // ConvertToMp3
            return ms.ToArray();
        }

        /// <summary>Payload HLS cũ: thiếu ConvertTsToMp4 và các field checkpoint.</summary>
        private static byte[] LegacyHlsPayload(string id, string tempDir)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(id);
            w.Write(tempDir ?? string.Empty);
            w.Write(8192L);
            w.Write(false); // Demuxed
            w.Write(0);     // SpeedLimit
            w.Write(0);     // AudioChunkCount
            w.Write(string.Empty);
            w.Write(0);     // VideoChunkCount
            w.Write(string.Empty);
            w.Write(0.0);   // Duration
            w.Write(string.Empty); // MuxedPlaylistUrl
            w.Write(string.Empty); // NonMuxedAudioPlaylistUrl
            w.Write(string.Empty); // NonMuxedVideoPlaylistUrl
            w.Write(false); // hasHeaders
            w.Write(false); // hasCookies
            w.Write(false); // hasProxy
            return ms.ToArray();
        }

        /// <summary>Chỉ thêm cờ ConvertTsToMp4: "EOF ngay sau bool" -> checkpoint rỗng.</summary>
        private static byte[] AppendFlagOnly(byte[] payload, bool convert)
        {
            using var ms = new MemoryStream();
            ms.Write(payload, 0, payload.Length);
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(convert);
            return ms.ToArray();
        }

        /// <summary>
        /// Thêm cờ và một nhóm checkpoint bị cắt: string đầu hoàn chỉnh, string sau có length prefix
        /// nhưng không đủ bytes.
        /// </summary>
        private static byte[] AppendTruncatedCheckpoint(byte[] payload, bool convert, string tsPath)
        {
            using var ms = new MemoryStream();
            ms.Write(payload, 0, payload.Length);
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(convert);
            w.Write(tsPath);
            // String dở dang: length prefix 7-bit hứa nhiều bytes hơn thực có.
            w.Write((byte)20);
            w.Write(new byte[] { 1, 2, 3 });
            return ms.ToArray();
        }

        private void WriteTransactedState(string stateFileName, byte[] payload)
        {
            var path = Path.Combine(tempDir, stateFileName + ".1");
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(4 + payload.Length), 0, 4);
            ms.Write(payload, 0, payload.Length);
            ms.Write(EndMarker, 0, EndMarker.Length);
            File.WriteAllBytes(path, ms.ToArray());
        }

        /// <summary>
        /// Fake runner cho orchestration ProbeTs: ghi nhận args và trả stderr/exit code theo lượt,
        /// không cần FFmpeg thật. Chỉ override seam <see cref="FFmpegMediaProcessor.RunTsProbe"/>;
        /// <c>ProbeTs</c> vẫn là bản thật (đỏ cho tới T008).
        /// </summary>
        private sealed class FakeProbeProcessor : FFmpegMediaProcessor
        {
            private readonly List<string[]> probeArgs = new();
            private readonly Queue<(int ExitCode, string[] Lines)> responses = new();

            public FakeProbeProcessor(params (int ExitCode, string[] Lines)[] responses)
            {
                foreach (var response in responses)
                {
                    this.responses.Enqueue(response);
                }
            }

            public IReadOnlyList<string[]> ProbeArgs => probeArgs;
            public Action<CancelFlag>? OnProbe;

            protected override MediaProcessingResult RunTsProbe(string[] args, CancelFlag cancellationToken,
                out List<string> stderrLines, out int exitCode)
            {
                probeArgs.Add(args);
                OnProbe?.Invoke(cancellationToken);
                if (responses.Count > 0)
                {
                    var response = responses.Dequeue();
                    stderrLines = response.Lines.ToList();
                    exitCode = response.ExitCode;
                }
                else
                {
                    stderrLines = new List<string>();
                    exitCode = 0;
                }
                return MediaProcessingResult.Success;
            }
        }

        private sealed class FakeMediaProcessor : BaseMediaProcessor
        {
            public int ProbeCalls;
            public int RemuxCalls;
            public bool ProbeIsTsVideo = true;
            public MediaProcessingResult ProbeResult = MediaProcessingResult.Success;
            public MediaProcessingResult RemuxResult = MediaProcessingResult.Success;
            public string? RemuxError;
            public Action<string>? OnRemux;

            public override MediaProcessingResult ProbeTs(string infile, bool hasTsSignature,
                CancelFlag cancellationToken, out bool isTsVideo)
            {
                ProbeCalls++;
                isTsVideo = ProbeIsTsVideo;
                return ProbeResult;
            }

            public override MediaProcessingResult RemuxTsToMp4(string infile, string outfile,
                CancelFlag cancellationToken, out long outFileSize)
            {
                RemuxCalls++;
                OnRemux?.Invoke(outfile);
                outFileSize = File.Exists(outfile) ? new FileInfo(outfile).Length : -1;
                LastError = RemuxResult == MediaProcessingResult.Success ? null : RemuxError;
                return RemuxResult;
            }

            public override MediaProcessingResult MergeAudioVideStream(string file1, string file2,
                string outfile, CancelFlag cancellationToken, out long outFileSize)
                => throw new NotSupportedException();

            public override MediaProcessingResult MergeHLSAudioVideStream(string segmentListFile,
                string outfile, CancelFlag cancellationToken, out long outFileSize)
                => throw new NotSupportedException();

            public override MediaProcessingResult ConvertToMp3Audio(string segmentListFile,
                string outfile, CancelFlag cancellationToken, out long outFileSize)
                => throw new NotSupportedException();
        }
    }
}
