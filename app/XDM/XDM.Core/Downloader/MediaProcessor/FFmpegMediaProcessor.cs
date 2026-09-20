using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using XDM.Core;
using XDM.Core.Downloader;
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
            var ret = this.ProcessMedia(args, outfile, cancellationToken);
            try
            {
                outFileSize = ret == MediaProcessingResult.Success ? new FileInfo(outfile).Length : -1;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        public override MediaProcessingResult MergeHLSAudioVideStream(string fileList, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            var args = CreateHLSMergeArgs(fileList, outfile);
            var ret = this.ProcessMedia(args, outfile, cancellationToken);
            try
            {
                outFileSize = ret == MediaProcessingResult.Success ? new FileInfo(outfile).Length : -1;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        public override MediaProcessingResult ConvertToMp3Audio(string infile, string outfile,
            CancelFlag cancellationToken, out long outFileSize)
        {
            var args = CreateMP3MergeArgs(infile, outfile);
            var ret = this.ProcessMedia(args, outfile, cancellationToken);
            try
            {
                outFileSize = ret == MediaProcessingResult.Success ? new FileInfo(outfile).Length : -1;
            }
            catch { outFileSize = -1; }
            return ret;
        }

        private static string[] CreateMergeArgs(string file1, string file2, string outfile)
        {
            var args = new string[] { "-i", file1, "-i", file2, "-acodec", "copy", "-vcodec", "copy",
                "-map", "0", "-map", "1", "-progress", "pipe:1", "-nostats", outfile, "-y" };
            return args;
        }

        private string[] CreateHLSMergeArgs(string file, string outfile)
        {
            var args = new string[] { "-f", "concat", "-safe", "0", "-i", file, "-auto_convert", "1", "-acodec", "copy", "-vcodec", "copy", "-progress", "pipe:1", "-nostats", outfile, "-y" };
            return args;
        }

        private string[] CreateMP3MergeArgs(string file, string outfile)
        {
            var args = new string[] { "-i", file, "-acodec", "libmp3lame", "-progress", "pipe:1", "-nostats", outfile, "-y" };
            return args;
        }

        private MediaProcessingResult ProcessMedia(string[] args, string outfile, CancelFlag cancellationToken)
        {
            try
            {

                var duration = 0L;
                var time = 0L;
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
                //ffmpeg writes both the "Duration:" banner and the human readable stats line to stderr
                pb.RedirectStandardError = true;

                using var proc = Process.Start(pb);
                if (proc == null)
                {
                    throw new Exception("FFmpeg process could not be started - Process.Start");
                }

                //-progress pipe:1 makes ffmpeg write machine readable key=value lines on stdout
                //(out_time_us / out_time_ms / total_size / progress=continue|end). Emit at most two
                //events per second, and never 100 here: the caller publishes the final 100 once the
                //output file is complete.
                void ReportProgress()
                {
                    if (duration <= 0) return;
                    var tick = Helpers.TickCount();
                    if (tick - lastTick < 500) return;
                    lastTick = tick;
                    var prg = (int)(time * 100 / duration);
                    if (prg < 0) prg = 0;
                    if (prg > 99) prg = 99;
                    UpdateProgress(prg, DownloadPhase.Merging);
                }

                proc.OutputDataReceived += (a, b) =>
                {
                    try
                    {
                        var line = b.Data;
                        if (line == null) return;
                        Log.Debug(line);
                        if (ParsingHelper.ParseKeyValuePair(line, '=', out var kv) && kv.HasValue)
                        {
                            switch (kv.Value.Key)
                            {
                                case "out_time_us":
                                case "out_time_ms":
                                    //both keys carry microseconds; ffmpeg reports a cumulative
                                    //timestamp, so assign instead of accumulating
                                    if (long.TryParse(kv.Value.Value, out long us) && us >= 0)
                                    {
                                        time = us / 1000000;
                                        ReportProgress();
                                    }
                                    break;
                                case "progress":
                                    ReportProgress();
                                    break;
                            }
                            return;
                        }
                        //fallback for ffmpeg builds without -progress
                        var ret2 = ParsingHelper.ParseTime(ParsingHelper.RxTime.Match(line));
                        if (ret2 > 0)
                        {
                            time = ret2;
                            ReportProgress();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, ex.Message);
                    }
                };

                //the "Duration:" banner is written to stderr
                proc.ErrorDataReceived += (a, b) =>
                {
                    try
                    {
                        var line = b.Data;
                        if (line != null)
                        {
                            Log.Debug(line);
                            if (duration <= 0)
                            {
                                var ret = ParsingHelper.ParseTime(ParsingHelper.RxDuration.Match(line));
                                if (ret > 0) duration = ret;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, ex.Message);
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var cancelled = false;
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                        }
                        //ExitCode is only valid after the process has exited
                        proc.WaitForExit();
                        break;
                    }
                    if (proc.WaitForExit(100))
                    {
                        proc.WaitForExit(); //see remarks section https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexit?view=net-6.0
                        break;
                    }
                }

                if (cancelled || cancellationToken.IsCancellationRequested)
                {
                    //a killed ffmpeg leaves a truncated file behind that would look like a good download
                    TryDeleteFile(outfile);
                    return MediaProcessingResult.Cancelled;
                }

                if (proc.ExitCode == 0)
                {
                    return MediaProcessingResult.Success;
                }
                Log.Debug("FFmpeg exitcode: " + proc.ExitCode);
                TryDeleteFile(outfile);
                return MediaProcessingResult.Failed;
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine(ex);
                return MediaProcessingResult.AppNotFound;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                TryDeleteFile(outfile);
                return MediaProcessingResult.Failed;
            }
        }

        private static void TryDeleteFile(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to delete partial media file: " + file);
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
