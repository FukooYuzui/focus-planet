using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusGarden.App;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "FocusGarden-AppearanceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        void Run(string name, Action<string> test)
        {
            var dir = Path.Combine(root, (passed + failed).ToString()); Directory.CreateDirectory(dir);
            try { test(dir); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
        }
        try
        {
            Run("empty data uses default without writing settings", dir =>
            {
                var appearance = new FocusAppearance(dir);
                Assert(appearance.BackgroundImage == null && appearance.LoadWarning == null && !File.Exists(Path.Combine(dir, "appearance.json")));
            });
            Run("valid import is frozen, persisted, copied and does not alter source", dir =>
            {
                var source = Path.Combine(dir, "original.PNG"); CreatePng(source, 4, 3);
                var before = SHA256.HashData(File.ReadAllBytes(source));
                var appearance = new FocusAppearance(Path.Combine(dir, "data")); appearance.SelectImage(source);
                Assert(appearance.BackgroundImage?.IsFrozen == true && appearance.ImageName == "original.PNG");
                Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))));
                Assert(Directory.GetFiles(Path.Combine(dir, "data", "backgrounds")).Length == 1);
                using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            Run("import survives moving or deleting the original and process restart", dir =>
            {
                var source = Path.Combine(dir, "original.png"); CreatePng(source, 4, 3);
                var data = Path.Combine(dir, "data"); new FocusAppearance(data).SelectImage(source); File.Delete(source);
                var reloaded = new FocusAppearance(data);
                Assert(reloaded.BackgroundImage != null && reloaded.ImageName == "original.png" && reloaded.LoadWarning == null);
            });
            Run("invalid image cannot replace current selection", dir =>
            {
                var source = Path.Combine(dir, "valid.png"); CreatePng(source, 4, 3);
                var appearance = new FocusAppearance(Path.Combine(dir, "data")); appearance.SelectImage(source);
                var previous = appearance.BackgroundImage;
                var invalid = Path.Combine(dir, "invalid.png"); File.WriteAllText(invalid, "not an image");
                Throws(() => appearance.SelectImage(invalid));
                Assert(ReferenceEquals(previous, appearance.BackgroundImage) && appearance.ImageName == "valid.png");
            });
            Run("oversize and unsupported extensions are rejected", dir =>
            {
                var large = Path.Combine(dir, "large.png"); using (var f = File.Create(large)) f.SetLength(25 * 1024 * 1024 + 1);
                var appearance = new FocusAppearance(Path.Combine(dir, "data"));
                Throws(() => appearance.SelectImage(large)); Throws(() => appearance.SelectImage(Path.Combine(dir, "x.svg")));
                Assert(appearance.BackgroundImage == null);
            });
            Run("decoded long edge is bounded without upscaling small files", dir =>
            {
                var source = Path.Combine(dir, "wide.png"); CreatePng(source, 3000, 1);
                var appearance = new FocusAppearance(Path.Combine(dir, "data")); appearance.SelectImage(source);
                var image = (BitmapSource)appearance.BackgroundImage!; Assert(image.PixelWidth <= 2560 && image.PixelHeight >= 1);
                CreatePng(source, 4, 3); appearance.SelectImage(source);
                image = (BitmapSource)appearance.BackgroundImage!; Assert(image.PixelWidth == 4 && image.PixelHeight == 3);
            });
            Run("missing managed image falls back with readable warning", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "appearance.json"), "{\"RelativePath\":\"backgrounds/missing.png\",\"ImageName\":\"missing.png\"}");
                var appearance = new FocusAppearance(dir); Assert(appearance.BackgroundImage == null && appearance.LoadWarning != null);
            });
            Run("invalid JSON and external paths safely fall back", dir =>
            {
                var path = Path.Combine(dir, "appearance.json"); File.WriteAllText(path, "broken json");
                Assert(new FocusAppearance(dir).LoadWarning != null);
                File.WriteAllText(path, "{\"RelativePath\":\"../outside.png\"}");
                Assert(new FocusAppearance(dir).LoadWarning != null);
            });
            Run("reset persists default and retains imported originals", dir =>
            {
                var source = Path.Combine(dir, "original.png"); CreatePng(source, 4, 3);
                var data = Path.Combine(dir, "data"); var appearance = new FocusAppearance(data); appearance.SelectImage(source); appearance.Reset();
                Assert(appearance.BackgroundImage == null && appearance.ImageName == null && appearance.LoadWarning == null);
                Assert(new FocusAppearance(data).BackgroundImage == null && File.Exists(source));
                Assert(Directory.GetFiles(Path.Combine(data, "backgrounds")).Length == 1);
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("FocusGarden-AppearanceTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a path outside the test temporary directory.");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine($"{passed} passed / {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }
    private static void CreatePng(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 110; pixels[i + 2] = 80; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
