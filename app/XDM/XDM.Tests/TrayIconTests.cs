using System;
using NUnit.Framework;
using XDM.GtkUI.Utils.Tray;

namespace XDM.Tests
{
    [TestFixture]
    public class TrayIconPixmapTests
    {
        [Test]
        public void FromRgba_WritesArgb32InNetworkByteOrder()
        {
            // Two pixels: opaque red, then half transparent blue.
            var pixels = new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 };

            var pixmap = TrayIconPixmap.FromRgba(pixels, 2, 1, 8, true);

            Assert.That(pixmap.Width, Is.EqualTo(2));
            Assert.That(pixmap.Height, Is.EqualTo(1));
            Assert.That(pixmap.Argb32, Is.EqualTo(new byte[] { 255, 255, 0, 0, 128, 0, 0, 255 }));
            Assert.That(pixmap.Argb32.Length, Is.EqualTo(pixmap.Width * pixmap.Height * 4));
        }

        [Test]
        public void FromRgba_AddsOpaqueAlphaWhenSourceHasNone()
        {
            var pixmap = TrayIconPixmap.FromRgba(new byte[] { 1, 2, 3 }, 1, 1, 3, false);

            Assert.That(pixmap.Argb32, Is.EqualTo(new byte[] { 255, 1, 2, 3 }));
        }

        [Test]
        public void FromRgba_HonoursRowPadding()
        {
            // 1x2 RGB image with a 5 byte stride: two padding bytes after each pixel.
            var pixels = new byte[] { 10, 20, 30, 99, 99, 40, 50, 60, 99, 99 };

            var pixmap = TrayIconPixmap.FromRgba(pixels, 1, 2, 5, false);

            Assert.That(pixmap.Argb32, Is.EqualTo(new byte[] { 255, 10, 20, 30, 255, 40, 50, 60 }));
        }

        [Test]
        public void FromRgba_RejectsBufferTooSmallForTheDescribedImage()
        {
            Assert.Throws<ArgumentException>(() => TrayIconPixmap.FromRgba(new byte[3], 2, 2, 8, true));
        }

        [Test]
        public void FromRgba_RejectsStrideSmallerThanOneRow()
        {
            Assert.Throws<ArgumentException>(() => TrayIconPixmap.FromRgba(new byte[64], 4, 4, 8, true));
        }

        [Test]
        public void FromRgba_RejectsEmptyDimensions()
        {
            Assert.Throws<ArgumentException>(() => TrayIconPixmap.FromRgba(new byte[4], 0, 1, 4, true));
            Assert.Throws<ArgumentException>(() => TrayIconPixmap.FromRgba(new byte[4], 1, 0, 4, true));
        }
    }

    [TestFixture]
    public class TrayMenuLayoutTests
    {
        [Test]
        public void Build_PublishesRootWithRestoreThenExit()
        {
            var layout = TrayMenuLayout.Build(1, "Restore Window", "Exit");

            Assert.That(SniMenuLayout.RootId, Is.EqualTo(0));
            Assert.That(layout.Revision, Is.EqualTo(1));
            Assert.That(layout.Items.Count, Is.EqualTo(2));
            Assert.That(layout.Items[0].Id, Is.EqualTo(SniMenuLayout.RestoreItemId));
            Assert.That(layout.Items[0].Label, Is.EqualTo("Restore Window"));
            Assert.That(layout.Items[0].Action, Is.EqualTo(TrayMenuItemAction.RestoreWindow));
            Assert.That(layout.Items[1].Id, Is.EqualTo(SniMenuLayout.ExitItemId));
            Assert.That(layout.Items[1].Label, Is.EqualTo("Exit"));
            Assert.That(layout.Items[1].Action, Is.EqualTo(TrayMenuItemAction.ExitApplication));
        }

        [Test]
        public void Build_UsesEnglishTextWhenTheLanguageFileHasNoEntry()
        {
            // TextResource.GetText returns an empty string for a key no language file defines; an empty
            // menu entry would be unusable.
            var layout = TrayMenuLayout.Build(1, string.Empty, "   ");

            Assert.That(layout.Items[0].Label, Is.EqualTo("Restore Window"));
            Assert.That(layout.Items[1].Label, Is.EqualTo("Exit"));
        }

        [Test]
        public void ActionFor_MapsIdsAndIgnoresUnknownOnes()
        {
            var layout = TrayMenuLayout.Build(1, "R", "E");

            Assert.That(layout.ActionFor(SniMenuLayout.RestoreItemId), Is.EqualTo(TrayMenuItemAction.RestoreWindow));
            Assert.That(layout.ActionFor(SniMenuLayout.ExitItemId), Is.EqualTo(TrayMenuItemAction.ExitApplication));
            Assert.That(layout.ActionFor(42), Is.Null);
        }
    }
}
