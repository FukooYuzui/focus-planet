using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusGarden.App;

/// <summary>Imports a validated background into the app's own data directory.</summary>
internal sealed class FocusAppearance
{
    private const long MaximumBytes = 25 * 1024 * 1024;
    private const int MaximumDecodeEdge = 2560;
    private readonly string dataDirectory;
    private readonly string backgroundsDirectory;
    private readonly string settingsFile;
    public ImageSource? BackgroundImage { get; private set; }
    public string? ImageName { get; private set; }
    public string? LoadWarning { get; private set; }

    public FocusAppearance(string dataDirectory)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        backgroundsDirectory = Path.Combine(this.dataDirectory, "backgrounds");
        settingsFile = Path.Combine(this.dataDirectory, "appearance.json");
        Load();
    }

    public void SelectImage(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp"))
            throw new ArgumentException("请选择 JPG、JPEG、PNG 或 BMP 图片。");
        var bytes = ReadBounded(sourcePath);
        var image = Decode(bytes);
        var fileName = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
        var storedPath = Path.Combine(backgroundsDirectory, fileName);
        Directory.CreateDirectory(backgroundsDirectory);
        // The imported bytes never overwrite or modify the user's selected file.
        // Replacing an existing managed copy also repairs a corrupt same-hash filename.
        WriteAtomically(storedPath, bytes);
        Save(new AppearanceData { RelativePath = Path.Combine("backgrounds", fileName), ImageName = Path.GetFileName(sourcePath) });
        BackgroundImage = image;
        ImageName = Path.GetFileName(sourcePath);
        LoadWarning = null;
    }

    public void Reset()
    {
        // Keep already imported files so resetting cannot destroy the user's image.
        Save(new AppearanceData());
        BackgroundImage = null;
        ImageName = null;
        LoadWarning = null;
    }

    private void Load()
    {
        if (!File.Exists(settingsFile)) return;
        try
        {
            if (new FileInfo(settingsFile).Length > 16 * 1024) throw new InvalidDataException("图片设置过大。");
            var data = JsonSerializer.Deserialize<AppearanceData>(File.ReadAllText(settingsFile));
            if (string.IsNullOrWhiteSpace(data?.RelativePath)) return;
            var resolved = Path.GetFullPath(Path.Combine(dataDirectory, data.RelativePath));
            var expectedPrefix = backgroundsDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("图片设置路径无效。");
            BackgroundImage = Decode(ReadBounded(resolved));
            ImageName = string.IsNullOrWhiteSpace(data.ImageName) ? Path.GetFileName(resolved) : Path.GetFileName(data.ImageName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileFormatException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            BackgroundImage = null;
            ImageName = null;
            LoadWarning = "原专注背景无法读取，已使用默认背景；可以重新选择图片。";
        }
    }

    private void Save(AppearanceData data)
    {
        Directory.CreateDirectory(dataDirectory);
        WriteAtomically(settingsFile, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumBytes)
            throw new ArgumentException("图片大小应大于 0，且不能超过 25 MB。");
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        using var inspection = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(inspection, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not (JpegBitmapDecoder or PngBitmapDecoder or BmpBitmapDecoder))
            throw new ArgumentException("这不是受支持的 JPG、PNG 或 BMP 图片。");
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new ArgumentException("图片尺寸过大，请选择不超过 1 亿像素的图片。");
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, MaximumDecodeEdge);
        else image.DecodePixelHeight = Math.Min(height, MaximumDecodeEdge);
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed class AppearanceData
    {
        public string? RelativePath { get; set; }
        public string? ImageName { get; set; }
    }
}
