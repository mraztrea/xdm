using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using XDM.Core;
using System.Diagnostics;
using System.Threading;
using XDM.Core.Util;
using TraceLog;

namespace XDM.Core.MediaProcessor
{
    public class FFmpegMediaProcessor : BaseMediaProcessor
    {
        public override MediaProcessingResult MergeAudioVideStream(string file1, string file2,
            string outfile, CancelFlag cancellationToken, out long outFileSize)
        {
            var args = CreateMergeArgs(file1, file2, outfile);
            var ret = this.ProcessMedia(args, cancellationToken);
            try
            {
                outFileSize = new FileInfo(outfile).Length;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        public override MediaProcessingResult MergeHLSAudioVideStream(string fileList, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            var args = CreateHLSMergeArgs(fileList, outfile);
            var ret = this.ProcessMedia(args, cancellationToken);
            try
            {
                outFileSize = new FileInfo(outfile).Length;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        public override MediaProcessingResult ConvertToMp3Audio(string infile, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            var args = CreateMP3MergeArgs(infile, outfile);
            var ret = this.ProcessMedia(args, cancellationToken);
            try
            {
                outFileSize = new FileInfo(outfile).Length;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        public override MediaProcessingResult RemuxTsToMp4(string infile, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.RemuxTsToMp4 is not implemented yet (T008).");
        }

        public override MediaProcessingResult ProbeTs(string infile, bool hasTsSignature,
            CancelFlag cancellationToken, out bool isTsVideo)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.ProbeTs is not implemented yet (T008).");
        }

        /// <summary>
        /// Chạy một lượt probe TS bằng FFmpeg và trả về các dòng stderr cùng exit code.
        /// Seam tối thiểu cho T008: <see cref="ProbeTs"/> gọi seam này cho lượt auto-probe rồi
        /// lượt forced (khi hint false và chưa xác nhận được video MPEGTS), kiểm tra cancellation
        /// trước lượt fallback. Lớp con giả trong test T005 override seam này để kiểm chứng
        /// orchestration mà không cần FFmpeg thật.
        /// Trả về <see cref="MediaProcessingResult.AppNotFound"/> khi thiếu binary FFmpeg,
        /// <see cref="MediaProcessingResult.Failed"/> khi không chạy được tiến trình, và
        /// <see cref="MediaProcessingResult.Success"/> khi tiến trình đã chạy xong bất kể exit code
        /// (exit code thật nằm ở <paramref name="exitCode"/>).
        /// </summary>
        protected virtual MediaProcessingResult RunTsProbe(string[] args, CancelFlag cancellationToken,
            out List<string> stderrLines, out int exitCode)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.RunTsProbe is not implemented yet (T008).");
        }

        /// <summary>
        /// Bộ dựng tham số probe/remux và parser output probe. Chữ ký được chốt ở đây để test T005
        /// nhắm vào hành vi; phần thân sẽ được T008 triển khai.
        /// </summary>
        internal static string[] CreateRemuxArgs(string infile, string outfile)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.CreateRemuxArgs is not implemented yet (T008).");
        }

        internal static string[] CreateProbeTsArgs(string infile, bool forceMpegTs)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.CreateProbeTsArgs is not implemented yet (T008).");
        }

        internal static bool ProbeOutputConfirmsTsVideo(IEnumerable<string> stderrLines)
        {
            throw new NotImplementedException("FFmpegMediaProcessor.ProbeOutputConfirmsTsVideo is not implemented yet (T008).");
        }

        private static string[] CreateMergeArgs(string file1, string file2, string outfile)
        {
            var args = new string[] { "-i", file1, "-i", file2, "-acodec", "copy", "-vcodec", "copy",
                "-map", "0", "-map", "1", outfile, "-y" };
            return args;
        }

        private string[] CreateHLSMergeArgs(string file, string outfile)
        {
            var args = new string[] { "-f", "concat", "-safe", "0", "-i", file, "-auto_convert", "1", "-acodec", "copy", "-vcodec", "copy", outfile, "-y" };
            return args;
        }

        private string[] CreateMP3MergeArgs(string file, string outfile)
        {
            var args = new string[] { "-i", file, "-acodec", "libmp3lame", outfile, "-y" };
            return args;
        }

        private MediaProcessingResult ProcessMedia(string[] args, CancelFlag cancellationToken)
        {
            this.LastError = null;
            try
            {

                var duration = 0L;
                var time = 0L;
                string? lastLogLine = null;
                var file = FindFFmpegBinary();

                Log.Debug($"{file} {string.Join(" ", args)}");

                var lastTick = Helpers.TickCount();
                var pb = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

#if NET5_0_OR_GREATER
                foreach (var arg in args)
                {
                    pb.ArgumentList.Add(arg);
                }
#else
                pb.Arguments = XDM.Compatibility.ProcessStartInfoHelper.ArgumentListToArgsString(args);
#endif
                pb.RedirectStandardOutput = true;
                pb.RedirectStandardError = true;

                using var proc = Process.Start(pb);
                if (proc == null)
                {
                    throw new Exception("FFmpeg process could not be started - Process.Start");
                }

                proc.OutputDataReceived += (a, b) =>
                {
                    try
                    {
                        var line = b.Data;
                        if (line != null)
                        {
                            Log.Debug(line);
                            if (duration == 0.0)
                            {
                                var md = ParsingHelper.RxDuration.Match(line);
                                var ret = ParsingHelper.ParseTime(md);
                                if (ret > 0) duration = ret;
                            }
                            var mt = ParsingHelper.RxTime.Match(line);
                            var ret2 = ParsingHelper.ParseTime(mt);
                            if (ret2 > 0)
                            {
                                time += ret2;

                                var tick = Helpers.TickCount();

                                if (duration > 0 && tick - lastTick > 1000)
                                {
                                    UpdateProgress((int)((time * 100) / duration));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, ex.Message);
                    }
                };

                proc.ErrorDataReceived += (a, b) =>
                {
                    var line = b.Data;
                    if (!string.IsNullOrEmpty(line))
                    {
                        Log.Debug(line);
                        lastLogLine = line;
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        proc.Kill();
                        break;
                    }
                    if (proc.WaitForExit(100))
                    {
                        proc.WaitForExit(); //see remarks section https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexit?view=net-6.0
                        break;
                    }
                }

                if (proc.ExitCode == 0)
                {
                    return MediaProcessingResult.Success;
                }
                Log.Debug("FFmpeg exitcode: " + proc.ExitCode);
                this.LastError = "ffmpeg exited with code " + proc.ExitCode +
                    (string.IsNullOrEmpty(lastLogLine) ? "" : ": " + lastLogLine);
                return MediaProcessingResult.Failed;
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine(ex);
                return MediaProcessingResult.Success;
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine(ex);
                return MediaProcessingResult.AppNotFound;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Log.Debug(ex, "FFmpeg failed to run");
                this.LastError = ex.Message;
                return MediaProcessingResult.Failed;
            }
        }

        public static string FindFFmpegBinary()
        {
            var executableNames =
                Environment.OSVersion.Platform == PlatformID.Win32NT ?
                new string[] { "ffmpeg-x86.exe", "ffmpeg.exe" } : new string[] { "ffmpeg" };
            foreach (var executableName in executableNames)
            {
                var path = Path.Combine(Config.AppDir, executableName);
                if (File.Exists(path))
                {
                    return path;
                }
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, executableName);
                if (File.Exists(path))
                {
                    return path;
                }
                var ffmpegPathEnvVar = Environment.GetEnvironmentVariable("FFMPEG_HOME");
                //Log.Debug("FFMPEG_HOME: " + ffmpegPathEnvVar);
                if (ffmpegPathEnvVar != null)
                {
                    path = Path.Combine(ffmpegPathEnvVar, executableName);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                path = PlatformHelper.FindExecutableFromSystemPath(executableName);
                if (path != null)
                {
                    return path;
                }
            }
            throw new FileNotFoundException("FFmpeg executable not found");
        }

        public static bool IsFFmpegInstalled()
        {
            try
            {
                FindFFmpegBinary();
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            return true;
        }
    }
}
