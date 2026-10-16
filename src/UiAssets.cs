using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace CodexPushover
{
    // The caller owns the returned clone.  Resource streams are never held open.
    public static class UiAssets
    {
        private const string IconResource = "CodexPushover.AppIcon.ico";
        private const string MarkResource = "CodexPushover.AppMark.png";

        public static Icon SmallIcon()
        {
            Size size = SystemInformation.SmallIconSize;
            return ReadIcon(Math.Max(16, Math.Max(size.Width, size.Height)));
        }

        public static Icon WindowIcon()
        {
            int size = Math.Max(SystemInformation.IconSize.Width, SystemInformation.IconSize.Height);
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                size = Math.Max(size, (int)Math.Round(32f * graphics.DpiX / 96f));
            return ReadIcon(Math.Max(32, size));
        }

        public static Image BrandImage()
        {
            using (Stream stream = Resource(MarkResource))
            using (Image image = Image.FromStream(stream))
            {
                Bitmap bitmap = image as Bitmap;
                return bitmap == null ? new Bitmap(image) : bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
            }
        }

        private static Icon ReadIcon(int side)
        {
            using (Stream stream = Resource(IconResource))
            using (Icon icon = new Icon(stream, new Size(side, side)))
                return (Icon)icon.Clone();
        }

        private static Stream Resource(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new InvalidOperationException("The application is missing an embedded UI asset: " + name);
            return stream;
        }
    }
}
