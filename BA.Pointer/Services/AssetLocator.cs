namespace BA.Pointer.Services;

public static class AssetLocator
{
    public static string GetBundledAssetPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"软件本地缺少 Assets\\{fileName}。", path);
        return path;
    }

    public static string GetBundledCursorPath()
    {
        return GetBundledAssetPath("PCIcon_MousePoint.png");
    }
}
