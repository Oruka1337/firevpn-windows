using System.Windows.Media;

namespace v2rayN.Common;

/// <summary>
/// FireVPN: круглые флаги стран для списка серверов. Windows не рисует флаги-эмодзи (выходит «FI»),
/// поэтому страну берём из эмодзи или названия города и рисуем флаг сами.
/// </summary>
internal static class FireFlags
{
    private static readonly Dictionary<string, ImageSource?> _cache = [];

    public static string? CountryCode(string? remarks) => FireCountries.CountryCode(remarks);

    public static string CountryName(string? code) => FireCountries.CountryName(code);

    public static string StripFlags(string? name) => FireCountries.StripFlags(name);

    /// <summary>
    /// Квадратная картинка флага (обрезается кругом в разметке); null — флага нет, показываем буквы
    /// </summary>
    public static ImageSource? Get(string? code)
    {
        if (code.IsNullOrEmpty())
        {
            return null;
        }
        if (_cache.TryGetValue(code, out var cached))
        {
            return cached;
        }
        var g = new DrawingGroup();
        var ok = Draw(g, code);
        ImageSource? img = null;
        if (ok)
        {
            g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 60, 60));
            img = new DrawingImage(g);
            img.Freeze();
        }
        _cache[code] = img;
        return img;
    }

    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    private static void Rect(DrawingGroup g, string color, double x, double y, double w, double h)
    {
        g.Children.Add(new GeometryDrawing(B(color), null, new RectangleGeometry(new Rect(x, y, w, h))));
    }

    private static void Horizontal(DrawingGroup g, params string[] colors)
    {
        var h = 60.0 / colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            Rect(g, colors[i], 0, i * h, 60, h + 0.5);
        }
    }

    private static void Vertical(DrawingGroup g, params string[] colors)
    {
        var w = 60.0 / colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            Rect(g, colors[i], i * w, 0, w + 0.5, 60);
        }
    }

    // скандинавский крест, сдвинутый к древку
    private static void Nordic(DrawingGroup g, string bg, string cross, string? inner = null)
    {
        Rect(g, bg, 0, 0, 60, 60);
        Rect(g, cross, 0, 23, 60, 14);
        Rect(g, cross, 16, 0, 14, 60);
        if (inner != null)
        {
            Rect(g, inner, 0, 26, 60, 8);
            Rect(g, inner, 19, 0, 8, 60);
        }
    }

    private static void Circle(DrawingGroup g, string color, double cx, double cy, double r)
    {
        g.Children.Add(new GeometryDrawing(B(color), null, new EllipseGeometry(new Point(cx, cy), r, r)));
    }

    private static bool Draw(DrawingGroup g, string code)
    {
        switch (code)
        {
            case "nl": Horizontal(g, "#AE1C28", "#FFFFFF", "#21468B"); break;
            case "de": Horizontal(g, "#000000", "#DD0000", "#FFCE00"); break;
            case "ru": Horizontal(g, "#FFFFFF", "#0039A6", "#D52B1E"); break;
            case "at": Horizontal(g, "#ED2939", "#FFFFFF", "#ED2939"); break;
            case "lv": Horizontal(g, "#9E3039", "#9E3039", "#FFFFFF", "#9E3039", "#9E3039"); break;
            case "lt": Horizontal(g, "#FDB913", "#006A44", "#C1272D"); break;
            case "ee": Horizontal(g, "#0072CE", "#000000", "#FFFFFF"); break;
            case "hu": Horizontal(g, "#CE2939", "#FFFFFF", "#477050"); break;
            case "bg": Horizontal(g, "#FFFFFF", "#00966E", "#D62612"); break;
            case "pl": Horizontal(g, "#FFFFFF", "#DC143C"); break;
            case "ua": Horizontal(g, "#0057B7", "#FFD700"); break;
            case "es": Horizontal(g, "#AA151B", "#F1BF00", "#F1BF00", "#AA151B"); break;
            case "fr": Vertical(g, "#0055A4", "#FFFFFF", "#EF4135"); break;
            case "it": Vertical(g, "#009246", "#FFFFFF", "#CE2B37"); break;
            case "ie": Vertical(g, "#169B62", "#FFFFFF", "#FF883E"); break;
            case "be": Vertical(g, "#000000", "#FDDA24", "#EF3340"); break;
            case "ro": Vertical(g, "#002B7F", "#FCD116", "#CE1126"); break;
            case "fi": Nordic(g, "#FFFFFF", "#003580"); break;
            case "se": Nordic(g, "#006AA7", "#FECC00"); break;
            case "dk": Nordic(g, "#C8102E", "#FFFFFF"); break;
            case "no": Nordic(g, "#BA0C2F", "#FFFFFF", "#00205B"); break;
            case "jp":
                Rect(g, "#FFFFFF", 0, 0, 60, 60);
                Circle(g, "#BC002D", 30, 30, 13);
                break;
            case "ch":
                Rect(g, "#DA291C", 0, 0, 60, 60);
                Rect(g, "#FFFFFF", 25, 14, 10, 32);
                Rect(g, "#FFFFFF", 14, 25, 32, 10);
                break;
            case "tr":
                Rect(g, "#E30A17", 0, 0, 60, 60);
                Circle(g, "#FFFFFF", 26, 30, 13);
                Circle(g, "#E30A17", 29.5, 30, 10.5);
                break;
            case "kz":
                Rect(g, "#00AFCA", 0, 0, 60, 60);
                Circle(g, "#FEC50C", 32, 28, 10);
                break;
            case "cz":
                Horizontal(g, "#FFFFFF", "#D7141A");
                g.Children.Add(new GeometryDrawing(B("#11457E"), null, Geometry.Parse("M0,0 L30,30 L0,60 Z")));
                break;
            case "us":
                for (var i = 0; i < 7; i++)
                {
                    Rect(g, i % 2 == 0 ? "#B22234" : "#FFFFFF", 0, i * 60.0 / 7, 60, 60.0 / 7 + 0.5);
                }
                Rect(g, "#3C3B6E", 0, 0, 28, 60.0 * 4 / 7);
                break;
            case "gb":
                Rect(g, "#012169", 0, 0, 60, 60);
                g.Children.Add(new GeometryDrawing(null, new Pen(B("#FFFFFF"), 10), Geometry.Parse("M0,0 L60,60 M60,0 L0,60")));
                g.Children.Add(new GeometryDrawing(null, new Pen(B("#C8102E"), 4), Geometry.Parse("M0,0 L60,60 M60,0 L0,60")));
                Rect(g, "#FFFFFF", 0, 22, 60, 16);
                Rect(g, "#FFFFFF", 22, 0, 16, 60);
                Rect(g, "#C8102E", 0, 25, 60, 10);
                Rect(g, "#C8102E", 25, 0, 10, 60);
                break;
            default:
                return false;
        }
        return true;
    }
}
