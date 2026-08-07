namespace BA.Pointer.Services;

public static class ColorParser
{
    public static System.Windows.Media.Color Parse(string? value, System.Windows.Media.Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var text = value.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            var converted = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text)!;
            return converted;
        }
        catch
        {
            return fallback;
        }
    }
}
