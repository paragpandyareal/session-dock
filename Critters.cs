using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentDock;

/// <summary>A small pixel creature generated deterministically from a seed (the session id).</summary>
public sealed class Critter
{
    public const int Size = 12;

    Critter(Color?[,] pixels, string name, Color body)
    {
        Pixels = pixels;
        Name = name;
        BodyColor = body;
    }

    public Color?[,] Pixels { get; }
    public string Name { get; }
    public Color BodyColor { get; }

    static readonly string[] Starts = ["Mo", "Pi", "Bu", "Ka", "Lu", "Zo", "Fi", "Ne", "Ta", "Ro", "Mi", "Pu", "Ko", "Su", "Bo", "Yu"];
    static readonly string[] Ends = ["chi", "po", "ru", "mo", "bi", "ki", "la", "no", "zu", "pip", "ka", "mu", "to", "ni", "bo", "li"];

    /// <param name="hue">Optional body hue; the dock spreads hues so critters on screen look different.</param>
    public static Critter Create(string seed, double? hue = null)
    {
        var rng = new Random(StableHash(seed));
        double randomHue = rng.NextDouble() * 360;
        hue ??= randomHue;
        double h = hue.Value % 360;
        var body = FromHsl(h, 0.62, 0.64);
        var shade = FromHsl(h, 0.55, 0.50);
        var belly = FromHsl(h, 0.55, 0.84);
        var outline = FromHsl(h, 0.45, 0.16);
        var cheek = Color.FromRgb(255, 140, 160);
        var eye = Color.FromRgb(28, 24, 40);

        // Build the left half (x 0..5) of a mask, then mirror it.
        var mask = new bool[Size, Size];
        double rx = 4.3 + rng.NextDouble() * 1.2;
        double ry = 3.7 + rng.NextDouble() * 0.9;
        double cy = 6.6;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size / 2; x++)
        {
            double dx = (x - 5.5) / rx, dy = (y - cy) / ry;
            double d = dx * dx + dy * dy;
            bool inside = d <= 1.0;
            if (d > 0.75 && d < 1.25 && rng.NextDouble() < 0.22) inside = !inside; // lumpy edges
            mask[x, y] = inside;
        }

        // Ears / antenna / horns.
        switch (rng.Next(4))
        {
            case 0: Set(mask, 2, 2); Set(mask, 2, 1); Set(mask, 3, 2); break;           // pointy ears
            case 1: Set(mask, 4, 1); Set(mask, 4, 0); Set(mask, 3, 0); break;           // antenna
            case 2: Set(mask, 2, 2); Set(mask, 3, 2); Set(mask, 2, 3); Set(mask, 1, 3); break; // round ears
        }
        // Little feet.
        Set(mask, 3, 11);
        Set(mask, 4, 11);

        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size / 2; x++)
            mask[Size - 1 - x, y] = mask[x, y];

        // Make sure the face area is solid.
        for (int y = 5; y <= 8; y++)
        for (int x = 2; x <= 9; x++)
            mask[x, y] = true;

        var pixels = new Color?[Size, Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (mask[x, y])
            {
                bool edgeBelow = y == Size - 1 || !mask[x, y + 1];
                bool edgeRight = x == Size - 1 || !mask[x + 1, y];
                bool inBelly = y >= 8 && x >= 4 && x <= 7;
                pixels[x, y] = inBelly ? belly : (edgeBelow || edgeRight) ? shade : body;
            }
        }
        // Outline around the body.
        var outlined = (Color?[,])pixels.Clone();
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (mask[x, y]) continue;
            if (Has(mask, x - 1, y) || Has(mask, x + 1, y) || Has(mask, x, y - 1) || Has(mask, x, y + 1))
                outlined[x, y] = outline;
        }

        // Face.
        int eyeY = 5 + rng.Next(2);
        int eyeX = 3 + rng.Next(2);
        outlined[eyeX, eyeY] = eye;
        outlined[Size - 1 - eyeX, eyeY] = eye;
        outlined[eyeX, eyeY + 1] = eye;
        outlined[Size - 1 - eyeX, eyeY + 1] = eye;
        if (rng.NextDouble() < 0.7)
        {
            outlined[eyeX - 1, eyeY + 2] = cheek;
            outlined[Size - eyeX, eyeY + 2] = cheek;
        }
        outlined[5, eyeY + 2] = outline;
        outlined[6, eyeY + 2] = outline;

        string name = Starts[rng.Next(Starts.Length)] + Ends[rng.Next(Ends.Length)];
        return new Critter(outlined, name, body);
    }

    public ImageSource ToImage()
    {
        var bmp = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32, null);
        var data = new int[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            if (Pixels[x, y] is Color c)
                data[y * Size + x] = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, Size, Size), data, Size * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    static void Set(bool[,] m, int x, int y) => m[x, y] = true;
    static bool Has(bool[,] m, int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && m[x, y];

    static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return (int)h;
        }
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        double m = l - c / 2;
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
