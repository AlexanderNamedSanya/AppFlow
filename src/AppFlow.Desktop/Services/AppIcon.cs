using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppFlow.Desktop.Services;

public static class AppIcon
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        if (AppFlow.Core.ActionTarget.Kind(path) == AppFlow.Core.TargetKind.Website) return Cache[path] = ActionIcon(true);
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return Cache[path] = AppFlow.Core.ActionTarget.IsScript(path) ? ActionIcon(false) : null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return Cache[path] = source;
        }
        catch { return Cache[path] = AppFlow.Core.ActionTarget.IsScript(path) ? ActionIcon(false) : null; }
    }

    private static ImageSource ActionIcon(bool website)
    {
        var geometry = Geometry.Parse(website
            ? "M 16,3 A 13,13 0 1 1 15.99,3 M 3,16 L 29,16 M 16,3 C 6,9 6,23 16,29 C 26,23 26,9 16,3"
            : "M 3,5 L 29,5 L 29,27 L 3,27 Z M 8,11 L 13,16 L 8,21 M 17,21 L 24,21");
        var image = new DrawingImage(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(85, 150, 225)), 2), geometry));
        image.Freeze();
        return image;
    }

    public static string DisplayName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }
}
