using SkiaSharp;

namespace OverBoardAgent.Core;

/// <summary>Lädt ein Senderlogo und rechnet es in ein quadratisches Bild im Format des Displays (RGB565) um.</summary>
public static class RadioLogo
{
    public const int Size = 192;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, byte[]?> Cache = [];

    static RadioLogo()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OverBoard");
    }

    /// <summary>Das Logo als Size × Size Pixel RGB565 (je 2 Byte, niederwertiges Byte zuerst). Null, wenn es sich nicht laden oder lesen lässt.</summary>
    public static async Task<byte[]?> LoadAsync(string url, string theme)
    {
        var key = url + "|" + theme;
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;

        byte[]? result = null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            result = Convert(bytes, theme);
            if (result == null) Log.Warn($"Logo nicht lesbar: {url}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Logo nicht ladbar ({url}): {ex.Message}");
        }
        lock (Cache) Cache[key] = result;
        return result;
    }

    private static byte[]? Convert(byte[] data, string theme)
    {
        using var source = SKBitmap.Decode(data);
        if (source == null || source.Width == 0 || source.Height == 0) return null;

        // Ins Quadrat einpassen (Seitenverhältnis bleibt), auf die Farbe der Kachel gesetzt: transparente Logos sehen so richtig aus
        var background = theme == "light" ? new SKColor(0xFF, 0xFF, 0xFF) : new SKColor(0x16, 0x1C, 0x24);
        using var target = new SKBitmap(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(background);
            var scale = Math.Min((float)Size / source.Width, (float)Size / source.Height);
            var w = source.Width * scale;
            var h = source.Height * scale;
            var dest = new SKRect((Size - w) / 2, (Size - h) / 2, (Size + w) / 2, (Size + h) / 2);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(source, dest, paint);
        }

        var pixels = target.Pixels;
        var output = new byte[Size * Size * 2];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var v = (ushort)(((p.Red & 0xF8) << 8) | ((p.Green & 0xFC) << 3) | (p.Blue >> 3));
            output[i * 2] = (byte)(v & 0xFF);
            output[i * 2 + 1] = (byte)(v >> 8);
        }
        return output;
    }
}
