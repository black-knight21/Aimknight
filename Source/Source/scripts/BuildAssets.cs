using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Resources;

class BuildAssets
{
    static void Main(string[] args)
    {
        string root = args[0], intermediate = args[1];
        Directory.CreateDirectory(intermediate);
        using (var bitmap = new Bitmap(64, 64))
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(111, 57, 184));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, 7))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 20, 15, 20, 49);
                g.DrawLines(pen, new[] {new Point(44, 15), new Point(23, 32), new Point(45, 49)});
            }
            // Store PNG in a standards-compliant ICO container (Vista and newer).
            using (var png = new MemoryStream())
            {
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                using (var file = new BinaryWriter(File.Create(Path.Combine(root, "grapher", "mouse.ico"))))
                {
                    file.Write((short)0); file.Write((short)1); file.Write((short)1);
                    file.Write((byte)64); file.Write((byte)64); file.Write((byte)0); file.Write((byte)0);
                    file.Write((short)1); file.Write((short)32); file.Write((int)png.Length); file.Write(22);
                    file.Write(png.ToArray());
                }
            }
        }
        foreach (var name in new[] {"Form1", "AboutBox", "Properties/Resources"})
        {
            string input = Path.Combine(root, "grapher", name + ".resx");
            string output = Path.Combine(intermediate, name.Replace('/', '.') + ".resources");
            using (var reader = new ResXResourceReader(input))
            using (var writer = new ResourceWriter(output))
            {
                reader.BasePath = Path.GetDirectoryName(input);
                foreach (DictionaryEntry entry in reader)
                {
                    if ((string)entry.Key == "$this.Icon") writer.AddResource((string)entry.Key, new Icon(Path.Combine(root, "grapher", "mouse.ico")));
                    else writer.AddResource((string)entry.Key, entry.Value);
                }
            }
        }
    }
}
