using System;
using System.Runtime.InteropServices;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>
    /// Icon artwork in the format the desktop shell expects: ARGB32 pixels in network (big-endian)
    /// byte order, see the StatusNotifierItem icon-pixmap chapter.
    /// </summary>
    public sealed class TrayIconPixmap
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Argb32 { get; }

        private TrayIconPixmap(int width, int height, byte[] argb32)
        {
            Width = width;
            Height = height;
            Argb32 = argb32;
        }

        /// <summary>
        /// Converts a raw RGB/RGBA buffer (as produced by Gdk.Pixbuf) into ARGB32 network byte order.
        /// </summary>
        public static TrayIconPixmap FromRgba(byte[] pixels, int width, int height, int rowStride, bool hasAlpha)
        {
            if (pixels == null)
            {
                throw new ArgumentNullException(nameof(pixels));
            }
            if (width <= 0)
            {
                throw new ArgumentException("Width must be positive", nameof(width));
            }
            if (height <= 0)
            {
                throw new ArgumentException("Height must be positive", nameof(height));
            }
            var channels = hasAlpha ? 4 : 3;
            var minStride = width * channels;
            if (rowStride < minStride)
            {
                throw new ArgumentException($"Row stride {rowStride} is smaller than {minStride}", nameof(rowStride));
            }
            if (pixels.Length < (long)(height - 1) * rowStride + minStride)
            {
                throw new ArgumentException("Pixel buffer is smaller than the described image", nameof(pixels));
            }

            var argb32 = new byte[width * height * 4];
            var destination = 0;
            for (var y = 0; y < height; y++)
            {
                var source = y * rowStride;
                for (var x = 0; x < width; x++, source += channels, destination += 4)
                {
                    argb32[destination] = hasAlpha ? pixels[source + 3] : (byte)255;
                    argb32[destination + 1] = pixels[source];
                    argb32[destination + 2] = pixels[source + 1];
                    argb32[destination + 3] = pixels[source + 2];
                }
            }
            return new TrayIconPixmap(width, height, argb32);
        }

        public static TrayIconPixmap FromPixbuf(Gdk.Pixbuf pixbuf)
        {
            if (pixbuf == null)
            {
                throw new ArgumentNullException(nameof(pixbuf));
            }
            var buffer = new byte[pixbuf.Rowstride * pixbuf.Height];
            Marshal.Copy(pixbuf.Pixels, buffer, 0, buffer.Length);
            return FromRgba(buffer, pixbuf.Width, pixbuf.Height, pixbuf.Rowstride, pixbuf.HasAlpha);
        }
    }
}
