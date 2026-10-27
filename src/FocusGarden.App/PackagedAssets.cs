using System.IO;

namespace FocusGarden.App;

/// <summary>Exports the bundled extension without depending on the executable's location.</summary>
public static class PackagedAssets
{
    private const string ResourcePrefix = "FocusGarden.BrowserExtension.";
    private static readonly string[] ExtensionFiles =
    [
        "background.js", "controller.js", "manifest.json", "popup.css",
        "popup.html", "popup.js", "rules.js", "README.md"
    ];
    private static readonly object ExportLock = new();

    /// <summary>
    /// Keeps a stable unpacked-extension path across upgrades. Only the eight bundled
    /// files are replaced; user files, focus records, backups and pairing keys are untouched.
    /// </summary>
    public static string EnsureBrowserExtension(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("扩展数据目录必须是绝对路径。", nameof(dataDirectory));

        var root = Path.GetFullPath(dataDirectory);
        var destination = Path.Combine(root, "browser-extension");
        lock (ExportLock)
        {
            // Read every resource before changing any files, including on first launch.
            var resources = ExtensionFiles.Select(name => (Name: name, Bytes: ReadResource(name))).ToArray();
            ValidateDirectoryChain(destination);
            foreach (var resource in resources)
                ValidateTargetFile(Path.Combine(destination, resource.Name));

            Directory.CreateDirectory(destination);
            ValidateDirectoryChain(destination);
            foreach (var resource in resources)
                WriteIfChanged(destination, resource.Name, resource.Bytes);
            return destination;
        }
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(PackagedAssets).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException("程序缺少浏览器扩展文件，请重新获取完整版本：" + name);
        using var content = new MemoryStream();
        stream.CopyTo(content);
        return content.ToArray();
    }

    private static void WriteIfChanged(string destination, string name, byte[] bytes)
    {
        ValidateDirectoryChain(destination);
        var target = Path.Combine(destination, name);
        ValidateTargetFile(target);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;

        // A new sibling file guarantees the final rename stays on the same volume.
        var temporary = Path.Combine(destination, ".focusgarden-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ValidateDirectoryChain(destination);
            ValidateTargetFile(target);
            if (File.Exists(target)) File.Replace(temporary, target, destinationBackupFileName: null);
            else File.Move(temporary, target);
        }
        finally
        {
            // Never follow a changed directory link while cleaning up a failed export.
            try
            {
                ValidateDirectoryChain(destination);
                ValidateTargetFile(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateDirectoryChain(string path)
    {
        var chain = new Stack<string>();
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            chain.Push(directory.FullName);
        foreach (var directory in chain)
        {
            var attributes = ExistingAttributes(directory);
            if (attributes is null) continue;
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new IOException("扩展目录不能经过符号链接或目录联接：" + directory);
            if ((attributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("扩展目录被同名文件占用：" + directory);
        }
    }

    private static void ValidateTargetFile(string path)
    {
        var attributes = ExistingAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new IOException("扩展文件不能是符号链接：" + path);
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new IOException("扩展文件位置被同名目录占用：" + path);
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
