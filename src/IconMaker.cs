using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Build-time only.  All final image data is embedded in the single application EXE.
internal static class IconMaker
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("An output ICO path is required.");
            string output = Path.GetFullPath(args[0]);
            string directory = Path.GetDirectoryName(output);
            string masterPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(directory, "icon-master.png");
            string markPath = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(directory, "app-mark.png");
            Directory.CreateDirectory(directory);
            using (Bitmap master = File.Exists(masterPath) ? ReadMaster(masterPath) : DrawMark(1024))
            {
                master.Save(markPath, ImageFormat.Png);
                List<byte[]> frames = new List<byte[]>();
                foreach (int size in Sizes)
                {
                    using (Bitmap frame = size <= 48 ? DrawMark(size) : Resize(master, size))
                    using (MemoryStream memory = new MemoryStream())
                    {
                        frame.Save(memory, ImageFormat.Png);
                        frames.Add(memory.ToArray());
                    }
                }
                using (FileStream file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(file))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)Sizes.Length);
                    int offset = 6 + Sizes.Length * 16;
                    for (int index = 0; index < Sizes.Length; index++)
                    {
                        int size = Sizes[index];
                        writer.Write((byte)(size == 256 ? 0 : size));
                        writer.Write((byte)(size == 256 ? 0 : size));
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                        writer.Write((ushort)1);
                        writer.Write((ushort)32);
                        writer.Write((uint)frames[index].Length);
                        writer.Write((uint)offset);
                        offset += frames[index].Length;
                    }
                    foreach (byte[] frame in frames) writer.Write(frame);
                }
                Console.WriteLine("Created 10 PNG-backed ICO frames (16-256 px) and a " + master.Width + " x " + master.Height + " PNG.");
                Console.WriteLine(File.Exists(masterPath) ? "Large frames use the supplied high-resolution master." : "Large frames use the vector fallback; supply icon-master.png to replace it.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Icon generation failed: " + error.Message);
            return 1;
        }
    }

    private static Bitmap ReadMaster(string path)
    {
        using (Image source = Image.FromFile(path))
        {
            // Preserve the source aspect ratio and alpha, without stretching or cropping.
            Bitmap bitmap = source as Bitmap;
            if (source.Width == source.Height && bitmap != null)
                return bitmap.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
            int side = Math.Max(source.Width, source.Height);
            Bitmap result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(Color.Transparent);
                graphics.DrawImageUnscaled(source, (side - source.Width) / 2, (side - source.Height) / 2);
            }
            return result;
        }
    }

    private static Bitmap Resize(Bitmap source, int side)
    {
        Bitmap result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(0, 0, side, side), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    private static Bitmap DrawMark(int side)
    {
        Bitmap result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        using (SolidBrush blue = new SolidBrush(Blue))
        using (SolidBrush white = new SolidBrush(Color.White))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float margin = side <= 48 ? Math.Max(1, (float)Math.Round(side * .05f)) : side * .05f;
            float width = side - margin * 2;
            using (GraphicsPath background = RoundedRectangle(margin, margin, width, width, side * .20f))
                graphics.FillPath(blue, background);
            graphics.ScaleTransform(side, side);
            using (GraphicsPath bell = new GraphicsPath())
            {
                bell.StartFigure();
                bell.AddBezier(.50f, .29f, .39f, .29f, .33f, .38f, .33f, .49f);
                bell.AddLine(.33f, .49f, .33f, .58f);
                bell.AddBezier(.33f, .58f, .33f, .61f, .30f, .65f, .275f, .675f);
                bell.AddBezier(.275f, .675f, .255f, .70f, .275f, .735f, .305f, .735f);
                bell.AddLine(.305f, .735f, .695f, .735f);
                bell.AddBezier(.695f, .735f, .725f, .735f, .745f, .70f, .725f, .675f);
                bell.AddBezier(.725f, .675f, .70f, .65f, .67f, .61f, .67f, .58f);
                bell.AddLine(.67f, .58f, .67f, .49f);
                bell.AddBezier(.67f, .49f, .67f, .38f, .61f, .29f, .50f, .29f);
                bell.CloseFigure();
                graphics.FillPath(white, bell);
            }
            graphics.FillEllipse(white, .46f, .245f, .08f, .08f);
            graphics.FillEllipse(white, .447f, .765f, .106f, .085f);
        }
        return result;
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
