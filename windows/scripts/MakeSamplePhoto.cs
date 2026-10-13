// Draws the sample picture Help > Check AI Description describes (#95): a red mug on a wooden table beside a small sign
// reading OPEN, against a wall. Photo Finder owns it outright (it's drawn here, with no people and nothing anyone else
// made), so it ships with the app and can appear in bug reports. Run it again only to change the picture; the result is
// committed:
//
//   dotnet run scripts\MakeSamplePhoto.cs                (from windows\, writes src\PhotoFinder\Assets\SamplePhoto.jpg)
//
// GDI+ rather than WPF: WPF's RenderTargetBitmap draws nothing while the desktop is locked.
#:package System.Drawing.Common@10.0.0
#:property TargetFramework=net10.0-windows
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

var path = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("src", "PhotoFinder", "Assets", "SamplePhoto.jpg"));
const int width = 1200, height = 800, tableTop = 470;

static Color C(string hex) => ColorTranslator.FromHtml(hex);
static Brush L(string from, string to, Rectangle area, float angle) => new LinearGradientBrush(area, C(from), C(to), angle);

using var bitmap = new Bitmap(width, height);
using (var g = Graphics.FromImage(bitmap))
{
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

    // The wall: warm plaster, lit from the upper left.
    var wall = new Rectangle(0, 0, width, tableTop);
    g.FillRectangle(L("#EFE4D2", "#C9B79C", wall, 35), wall);

    // The table: planks of wood with grain, lighter towards the front.
    var table = new Rectangle(0, tableTop, width, height - tableTop);
    g.FillRectangle(L("#8A5A32", "#B07A48", table, 90), table);
    var random = new Random(7);
    using var seam = new Pen(C("#5E3A1E"), 3);
    using var grain = new Pen(Color.FromArgb(70, 70, 40, 15), 1.5f);
    for (var y = tableTop; y < height; y += 66)
    {
        g.DrawLine(seam, 0, y, width, y);
        for (var i = 0; i < 9; i++)
        {
            var gy = y + 8 + (float)random.NextDouble() * 50;
            g.DrawBezier(grain, 0, gy, 300, gy - 6 + (float)random.NextDouble() * 12, 800, gy - 6 + (float)random.NextDouble() * 12, width, gy);
        }
    }
    // Where the table meets the wall.
    var edge = new Rectangle(0, tableTop - 18, width, 18);
    using (var shade = new LinearGradientBrush(edge, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(85, 0, 0, 0), 90f))
        g.FillRectangle(shade, edge);

    // The mug's shadow, then the mug: a handle, a red body, a highlight, the rim and the coffee.
    using (var shadow = new SolidBrush(Color.FromArgb(90, 30, 15, 5))) g.FillEllipse(shadow, 295, 618, 350, 60);
    var handleArea = new Rectangle(518, 438, 144, 164);
    using (var handle = new Pen(L("#B3161B", "#7A0C10", handleArea, 45), 34)) g.DrawEllipse(handle, handleArea);
    var bodyArea = new Rectangle(300, 380, 300, 260);
    using (var body = Rounded(bodyArea, 26)) g.FillPath(L("#E0262C", "#8E1014", bodyArea, 0), body);
    using (var shine = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(shine, 330, 405, 34, 205);
    using (var rim = new SolidBrush(C("#9E1217"))) g.FillEllipse(rim, 300, 358, 300, 52);
    var coffee = new Rectangle(318, 368, 264, 36);
    g.FillEllipse(L("#3B1A0B", "#5A2C14", coffee, 45), coffee);

    // The sign: a wooden frame on a little stand, a dark board, and OPEN in white.
    using (var shadow = new SolidBrush(Color.FromArgb(80, 30, 15, 5))) g.FillEllipse(shadow, 780, 644, 300, 36);
    using (var wood = new SolidBrush(C("#6B4423")))
    {
        g.FillRectangle(wood, 905, 560, 30, 100);
        g.FillRectangle(wood, 840, 650, 160, 16);
    }
    using (var frame = Rounded(new Rectangle(770, 330, 320, 240), 12)) g.FillPath(new SolidBrush(C("#7A4E2A")), frame);
    using (var board = Rounded(new Rectangle(790, 350, 280, 200), 6)) g.FillPath(new SolidBrush(C("#1F2A24")), board);
    using var font = new Font("Segoe UI", 78, FontStyle.Bold, GraphicsUnit.Pixel);
    using var chalk = new SolidBrush(C("#F5F1E6"));
    using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    g.DrawString("OPEN", font, chalk, new RectangleF(790, 350, 280, 200), centred);
}

var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
using var quality = new EncoderParameters(1);
quality.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
bitmap.Save(path, jpeg, quality);
Console.WriteLine($"Wrote {path} ({new FileInfo(path).Length} bytes)");

static GraphicsPath Rounded(Rectangle r, int radius)
{
    var d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}
