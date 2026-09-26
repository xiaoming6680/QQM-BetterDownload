using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace QqmBetterDownload {
    internal static class BrandIcon {
        internal static Icon Load(int side = 32) {
            using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("brand.ico"))
            using (var icon = new Icon(data, new Size(side, side))) return (Icon)icon.Clone();
        }
        internal static Bitmap Raster(int side) {
            // Read the PNG frame directly. .NET Framework's Icon.ToBitmap can
            // discard PNG icon alpha, although Explorer displays the icon fine.
            var pixels = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("brand.ico"))
            using (var reader = new BinaryReader(data)) {
                reader.ReadUInt16(); reader.ReadUInt16(); int count = reader.ReadUInt16();
                uint offset = 0, length = 0; int best = Int32.MaxValue;
                for (int i = 0; i < count; i++) {
                    int width = reader.ReadByte(); if (width == 0) width = 256;
                    reader.ReadBytes(7); uint bytes = reader.ReadUInt32(), at = reader.ReadUInt32();
                    int score = width >= side ? width - side : 1000 + side - width;
                    if (score < best) { best = score; offset = at; length = bytes; }
                }
                data.Position = offset;
                using (var png = new MemoryStream(reader.ReadBytes((int)length)))
                using (var source = new Bitmap(png))
                using (var graphics = Graphics.FromImage(pixels)) graphics.DrawImage(source, new Rectangle(0, 0, side, side));
            }
            return pixels;
        }
    }
}
