// Generates the GitPersona app icon: Assets/app.png, app.ico (Windows) and app.icns (macOS).
// Usage: dotnet run --project tools/IconGen -- <output-dir>
using SkiaSharp;

var outDir = args.Length > 0 ? args[0] : "Assets";
Directory.CreateDirectory(outDir);

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    float s = size;

    // rounded-square background with a blue -> violet gradient
    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(s, s),
            [SKColor.Parse("#2563EB"), SKColor.Parse("#7C3AED")], SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRoundRect(new SKRect(s * .04f, s * .04f, s * .96f, s * .96f), s * .22f), bg);
    }

    // a git-graph glyph: trunk with a branch, standing for "several accounts, one place"
    using var line = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = s * .075f, StrokeCap = SKStrokeCap.Round };
    using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
    using var hole = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColor.Parse("#4F46E5") };
    float x1 = s * .36f, x2 = s * .66f, top = s * .24f, bottom = s * .76f, mid = s * .42f;
    c.DrawLine(x1, top, x1, bottom, line);
    using (var path = new SKPath())
    {
        path.MoveTo(x1, s * .62f);
        path.CubicTo(x1, s * .5f, x2, s * .56f, x2, mid);
        c.DrawPath(path, line);
    }
    foreach (var (x, y) in new[] { (x1, top), (x1, bottom), (x2, mid) })
    {
        c.DrawCircle(x, y, s * .095f, fill);
        c.DrawCircle(x, y, s * .045f, hole);
    }

    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
var pngs = icoSizes.ToDictionary(sz => sz, Render);

File.WriteAllBytes(Path.Combine(outDir, "app.png"), Render(512));

// ICO with PNG-compressed entries (supported since Windows Vista)
using (var ms = new MemoryStream())
using (var w = new BinaryWriter(ms))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)icoSizes.Length);
    var offset = 6 + 16 * icoSizes.Length;
    foreach (var sz in icoSizes)
    {
        w.Write((byte)(sz == 256 ? 0 : sz)); w.Write((byte)(sz == 256 ? 0 : sz));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(pngs[sz].Length); w.Write(offset);
        offset += pngs[sz].Length;
    }
    foreach (var sz in icoSizes) w.Write(pngs[sz]);
    File.WriteAllBytes(Path.Combine(outDir, "app.ico"), ms.ToArray());
}

// ICNS with PNG payloads: ic07=128, ic08=256, ic09=512, ic10=1024
using (var ms = new MemoryStream())
using (var w = new BinaryWriter(ms))
{
    (string Type, int Size)[] entries = [("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
    var payloads = entries.Select(e => (e.Type, Data: Render(e.Size))).ToList();
    static void BE(BinaryWriter w, int v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
    w.Write("icns"u8.ToArray());
    BE(w, 8 + payloads.Sum(p => 8 + p.Data.Length));
    foreach (var (type, data) in payloads) { w.Write(System.Text.Encoding.ASCII.GetBytes(type)); BE(w, 8 + data.Length); w.Write(data); }
    File.WriteAllBytes(Path.Combine(outDir, "app.icns"), ms.ToArray());
}

Console.WriteLine($"Wrote app.png, app.ico, app.icns to {Path.GetFullPath(outDir)}");
