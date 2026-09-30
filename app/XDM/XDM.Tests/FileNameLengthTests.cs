using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using XDM.Core.Util;

namespace XDM.Tests
{
    [TestFixture]
    public class FileNameLengthTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "xdm-name-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(folder, true);
        }

        private static int Measure(string name)
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? name.Length : Encoding.UTF8.GetByteCount(name);
        }

        [Test]
        public void FitFileNameToFolder_KeepsShortName()
        {
            Assert.That(FileHelper.FitFileNameToFolder("video.mp4", folder), Is.EqualTo("video.mp4"));
        }

        [Test]
        public void FitFileNameToFolder_ShortensLongNameAndKeepsExtension()
        {
            var name = new string('a', 400) + ".mp4";

            var result = FileHelper.FitFileNameToFolder(name, folder);

            Assert.That(result, Does.EndWith(".mp4"));
            Assert.That(Measure(result), Is.LessThanOrEqualTo(240));
            Assert.That(result, Does.StartWith("aaaa"));
        }

        [Test]
        public void FitFileNameToFolder_CountsMultiByteCharacters()
        {
            // 100 Vietnamese characters: ~300 UTF-8 bytes, over the 255 byte limit on Linux
            var name = string.Concat(System.Linq.Enumerable.Repeat("Việt", 25)) + ".mkv";

            var result = FileHelper.FitFileNameToFolder(name, folder);

            Assert.That(result, Does.EndWith(".mkv"));
            Assert.That(Measure(result), Is.LessThanOrEqualTo(240));
            File.WriteAllText(Path.Combine(folder, result), "x");
        }

        [Test]
        public void FitFileNameToFolder_DoesNotSplitSurrogatePairs()
        {
            var name = string.Concat(System.Linq.Enumerable.Repeat("😀", 100)) + ".txt";

            var result = FileHelper.FitFileNameToFolder(name, folder);

            Assert.That(result, Does.EndWith(".txt"));
            var stem = result.Substring(0, result.Length - 4);
            Assert.That(stem.Length % 2, Is.EqualTo(0));
            Assert.That(char.IsHighSurrogate(stem[stem.Length - 2]), Is.True);
        }

        [Test]
        public void FitFileNameToFolder_TreatsLongSuffixAsPartOfName()
        {
            var name = "Chapter 1. " + new string('b', 300);

            var result = FileHelper.FitFileNameToFolder(name, folder);

            Assert.That(result, Does.StartWith("Chapter 1. bbb"));
            Assert.That(Measure(result), Is.LessThanOrEqualTo(240));
        }

        [Test]
        public void GetUniqueFileName_ShortensAndAddsCounterWithinLimit()
        {
            var name = new string('c', 400) + ".zip";
            var first = FileHelper.GetUniqueFileName(name, folder);
            File.WriteAllText(Path.Combine(folder, first), "x");

            var second = FileHelper.GetUniqueFileName(name, folder);

            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(second, Does.EndWith("_1.zip"));
            Assert.That(Measure(second), Is.LessThanOrEqualTo(240));
            File.WriteAllText(Path.Combine(folder, second), "x");
        }
    }
}
