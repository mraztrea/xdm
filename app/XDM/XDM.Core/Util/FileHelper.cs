using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TraceLog;

namespace XDM.Core.Util
{
    public static class FileHelper
    {
        public static readonly Regex RxFileWithinQuote = new Regex("\\\"(.*)\\\"");
        public static string? SanitizeFileName(string fileName)
        {
            if (fileName == null) return fileName;
            var file = fileName.Split('/').Last();
            file = fileName.Split('\\').Last();
            return string.Join("_", file.Split(Path.GetInvalidFileNameChars()));
        }

        /// <summary>
        /// Creates the file that receives the assembled download. Creation failures
        /// (missing folder, no permission, read-only or full disk) are reported as
        /// <see cref="ErrorCode.TargetFileCreateFailed"/> instead of a generic error.
        /// </summary>
        public static FileStream CreateTargetFile(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Create, FileAccess.Write);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CreateTargetFile :: " + path);
                throw new AssembleFailedException(ErrorCode.TargetFileCreateFailed, ex.Message, ex);
            }
        }

        public static string GetDownloadFolderByFileName(string file)
        {
            try
            {
                var ext = Path.GetExtension(file)?.ToUpperInvariant();
                foreach (var category in Config.Instance.Categories)
                {
                    if (ext != null && category.FileExtensions.Contains(ext))
                    {
                        return category.DefaultFolder;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error");
            }
            return Config.Instance.DefaultDownloadFolder;
        }



        public static bool AddFileExtension(string name, string contentType, out string nameWithExt)
        {
            name = SanitizeFileName(name);
            if (name.EndsWith("."))
            {
                name = name.TrimEnd('.');
            }
            if (string.IsNullOrEmpty(contentType))
            {
                nameWithExt = name;
                return false;
            }
            if (contentType == "text/html")
            {
                nameWithExt = name + ".html";
                return true;
            }
            else
            {
                try
                {
                    var ext = MimeTypes.Get(contentType.ToLowerInvariant());
                    if (!string.IsNullOrEmpty(ext))
                    {
                        var prevExt = Path.GetExtension(name);
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
                        if (!("." + ext).Equals(prevExt, StringComparison.InvariantCultureIgnoreCase))
                        {
                            nameWithExt = nameWithoutExt + "." + ext;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error in AddFileExtension");
                }

                nameWithExt = name;
                return true;
            }
        }

        public static string GetFileName(Uri uri, string contentType = null)
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(name))
            {
                name = uri.Host.Replace('.', '_');
            }
            name = SanitizeFileName(name);
            if (string.IsNullOrEmpty(contentType))
            {
                return name;
            }

            if (contentType == "text/html")
            {
                return Path.ChangeExtension(name, ".html");
            }
            else
            {
                if (!Path.HasExtension(name))
                {
                    var ext = MimeTypes.Get(contentType.ToLowerInvariant());
                    if (!string.IsNullOrEmpty(ext))
                    {
                        name += "." + ext;
                    }
                }
                return name;
            }
        }

        public static string GetUniqueFileName(string file, string folder)
        {
            SplitFileName(file, out var name, out var ext);
            var candidate = FitFileName(name, ext, folder);
            var count = 0;
            while (File.Exists(Path.Combine(folder, candidate)))
            {
                count++;
                candidate = FitFileName(name, "_" + count + ext, folder);
            }
            return candidate;
        }

        /*
         * Longest file name we produce. The file system limit is 255 (UTF-8 bytes on
         * Linux/macOS, UTF-16 chars on Windows); the headroom covers the "_N" suffix
         * added on conflict and the "1_"/"2_" + container extension temp files.
         */
        private const int MaxFileNameLength = 240;
        private const int MaxPathLengthWindows = 259;
        private const int MaxPathLengthUnix = 4095;
        private const int MaxExtensionLength = 16;
        private const int MinStemLength = 8;

        /// <summary>
        /// Shortens <paramref name="file"/> (keeping its extension) so that it and
        /// <paramref name="folder"/>/<paramref name="file"/> stay within the file system
        /// limits. Long titles, especially non-ASCII ones, otherwise make the output
        /// file impossible to create.
        /// </summary>
        public static string FitFileNameToFolder(string file, string folder)
        {
            if (string.IsNullOrEmpty(file)) return file;
            SplitFileName(file, out var name, out var ext);
            return FitFileName(name, ext, folder);
        }

        private static void SplitFileName(string file, out string name, out string ext)
        {
            ext = Path.GetExtension(file) ?? string.Empty;
            if (ext.Length > MaxExtensionLength || ext.Contains(" "))
            {
                //not a real extension, e.g. "Part 1. Some long title"
                ext = string.Empty;
            }
            name = file.Substring(0, file.Length - ext.Length);
        }

        private static string FitFileName(string name, string tail, string folder)
        {
            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var maxPath = isWindows ? MaxPathLengthWindows : MaxPathLengthUnix;
            var folderLength = string.IsNullOrEmpty(folder) ? 0 :
                MeasureFileNameLength(Path.Combine(folder, "x"), isWindows) - 1;
            var limit = Math.Min(MaxFileNameLength, maxPath - folderLength);
            var tailLength = MeasureFileNameLength(tail, isWindows);
            if (limit - tailLength < MinStemLength)
            {
                //the folder alone is too long, shortening the name cannot help much
                limit = MaxFileNameLength;
            }

            var file = name + tail;
            if (MeasureFileNameLength(file, isWindows) <= limit)
            {
                return file;
            }

            var budget = Math.Max(limit - tailLength, MinStemLength);
            var stem = new StringBuilder();
            var used = 0;
            var elements = StringInfo.GetTextElementEnumerator(name);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var length = MeasureFileNameLength(element, isWindows);
                if (used + length > budget) break;
                stem.Append(element);
                used += length;
            }
            var shortName = stem.ToString().TrimEnd(' ', '.');
            if (shortName.Length == 0)
            {
                shortName = "download";
            }
            var result = shortName + tail;
            Log.Debug($"File name too long, shortened: '{file}' -> '{result}'");
            return result;
        }

        private static int MeasureFileNameLength(string text, bool isWindows)
        {
            return isWindows ? text.Length : Encoding.UTF8.GetByteCount(text);
        }

        public static string GetFileNameFromQuote(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            var matcher = RxFileWithinQuote.Match(text);
            if (matcher.Success)
            {
                return matcher.Groups[1].Value;
            }
            return null;
        }

        public static string QuoteFilePathIfNeeded(string file)
        {
            if (file.Contains(" "))
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT ? $"\"{file}\"" : $"\"{file}\"";
            }
            return file;
        }
    }
}
