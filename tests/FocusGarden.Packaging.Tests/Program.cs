using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusGarden.App;

// This executable references the actual App assembly, but never constructs App/MainWindow.
// Every filesystem fixture is freshly generated under one guarded temporary directory.
string[] names = ["background.js", "controller.js", "manifest.json", "popup.css", "popup.html", "popup.js", "rules.js", "README.md"];
var expected = names.ToDictionary(name => name, ExpectedBytes, StringComparer.Ordinal);
var results = new List<TestResult>();
var junctions = new List<string>();
var tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
var scratch = Path.Combine(tempParent, "FocusPlanet-Packaging-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int outputIndex = Array.IndexOf(args, "--output-dir");
string? outputDirectory = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : null;

try
{
    Run("actual App assembly contains exactly the eight production extension resources", () =>
    {
        const string prefix = "FocusGarden.BrowserExtension.";
        var actual = typeof(PackagedAssets).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => name[prefix.Length..]);
        Sequence(names.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    });

    Run("fresh export creates all eight exact source files at the canonical stable path", () =>
    {
        var directory = Path.Combine(NewDirectory("fresh"), "not-yet-created", "data");
        True(!Directory.Exists(directory), "The fresh fixture must not already exist.");
        var exported = PackagedAssets.EnsureBrowserExtension(directory);
        Equal(Path.Combine(Path.GetFullPath(directory), "browser-extension"), exported);
        Sequence(names.Order(StringComparer.Ordinal), Directory.GetFiles(exported).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        CheckBytes(exported);
        Equal(0, Directory.GetDirectories(exported).Length);
    });

    Run("exported manifest keeps source version and resolves its packaged browser entry points", () =>
    {
        var exported = PackagedAssets.EnsureBrowserExtension(NewDirectory("manifest"));
        using var actual = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(exported, "manifest.json")));
        using var source = JsonDocument.Parse(expected["manifest.json"]);
        var manifest = actual.RootElement;
        Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        string version = manifest.GetProperty("version").GetString()!;
        Equal(source.RootElement.GetProperty("version").GetString(), version);
        True(Version.TryParse(version, out _), "The extension version must be browser-compatible.");
        Equal("module", manifest.GetProperty("background").GetProperty("type").GetString());
        foreach (var entry in new[] { manifest.GetProperty("background").GetProperty("service_worker").GetString()!, manifest.GetProperty("action").GetProperty("default_popup").GetString()! })
        {
            True(names.Contains(entry, StringComparer.Ordinal), "Manifest entry point is not one of the bundled files: " + entry);
            True(File.Exists(Path.Combine(exported, entry)), "Manifest entry point is missing: " + entry);
        }
    });

    Run("repeated export is byte and timestamp idempotent and keeps non-bundled files", () =>
    {
        var directory = NewDirectory("idempotent");
        AddSyntheticPersonalFiles(directory);
        var exported = PackagedAssets.EnsureBrowserExtension(directory);
        File.WriteAllText(Path.Combine(exported, "my-notes.txt"), "Synthetic user-owned file.");
        Directory.CreateDirectory(Path.Combine(exported, "custom"));
        File.WriteAllBytes(Path.Combine(exported, "custom", "keep.bin"), [9, 0, 7, 2]);
        var fixedTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (string name in names) File.SetLastWriteTimeUtc(Path.Combine(exported, name), fixedTime);
        var before = Snapshot(directory);
        Equal(exported, PackagedAssets.EnsureBrowserExtension(directory));
        Equal(exported, PackagedAssets.EnsureBrowserExtension(directory));
        Sequence(before, Snapshot(directory));
        CheckBytes(exported);
    });

    Run("upgrade repairs stale or missing bundled files while retaining synthetic personal data", () =>
    {
        var directory = NewDirectory("upgrade");
        AddSyntheticPersonalFiles(directory);
        var personalBefore = Snapshot(directory);
        var exported = PackagedAssets.EnsureBrowserExtension(directory);
        File.WriteAllBytes(Path.Combine(exported, "background.js"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(exported, "manifest.json"), "{\"version\":\"0.0.1\"}");
        File.Delete(Path.Combine(exported, "popup.css"));
        File.WriteAllText(Path.Combine(exported, "do-not-remove.txt"), "Keep my extension notes.");
        PackagedAssets.EnsureBrowserExtension(directory);
        CheckBytes(exported);
        Equal("Keep my extension notes.", File.ReadAllText(Path.Combine(exported, "do-not-remove.txt")));
        Sequence(personalBefore, Snapshot(directory, "browser-extension"));
    });

    Run("a failed atomic replacement preserves old bytes and removes its temporary sibling", () =>
    {
        var directory = NewDirectory("locked-replacement");
        AddSyntheticPersonalFiles(directory);
        var exported = PackagedAssets.EnsureBrowserExtension(directory);
        var target = Path.Combine(exported, "background.js");
        File.WriteAllText(target, "Synthetic old extension bytes which must survive a failed update.");
        var before = Snapshot(directory);
        // Reads remain possible. Replacement/deletion is denied until this handle closes,
        // exercising failure after writing the complete sibling file, not a mock failure.
        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(directory));
            Sequence(before, Snapshot(directory));
        }
        PackagedAssets.EnsureBrowserExtension(directory);
        CheckBytes(exported);
    });

    Run("all existing file positions are validated before any earlier resource is replaced", () =>
    {
        var directory = NewDirectory("occupied-resource");
        var exported = PackagedAssets.EnsureBrowserExtension(directory);
        File.WriteAllText(Path.Combine(exported, "background.js"), "Old first file must not change.");
        File.Delete(Path.Combine(exported, "popup.html"));
        Directory.CreateDirectory(Path.Combine(exported, "popup.html"));
        File.WriteAllText(Path.Combine(exported, "popup.html", "keep.txt"), "Synthetic directory occupant.");
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(directory));
        Sequence(before, Snapshot(directory));
    });

    Run("a file occupying the data directory or one of its ancestors is retained", () =>
    {
        var directory = NewDirectory("file-directory");
        var occupied = Path.Combine(directory, "file-not-directory");
        File.WriteAllText(occupied, "Synthetic file, never replace it with a directory.");
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(occupied));
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(Path.Combine(occupied, "missing-child")));
        Sequence(before, Snapshot(directory));
    });

    Run("a file occupying browser-extension is retained without touching personal files", () =>
    {
        var directory = NewDirectory("file-extension-directory");
        AddSyntheticPersonalFiles(directory);
        File.WriteAllText(Path.Combine(directory, "browser-extension"), "Synthetic occupied path.");
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(directory));
        Sequence(before, Snapshot(directory));
    });

    Run("relative and drive-relative paths are refused without writing in the working directory", () =>
    {
        var directory = NewDirectory("invalid-paths");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var before = Snapshot(directory);
            foreach (var invalid in new string?[] { null, "", " ", ".", "relative-data", "..\\escape", "C:relative-data" })
                Throws<ArgumentException>(() => PackagedAssets.EnsureBrowserExtension(invalid!));
            Sequence(before, Snapshot(directory));
        }
        finally { Environment.CurrentDirectory = previous; }
    });

    Run("absolute dot segments normalize within the explicitly supplied synthetic location", () =>
    {
        var directory = NewDirectory("canonical-path");
        var supplied = Path.Combine(directory, "unused", "..", "actual", ".");
        var exported = PackagedAssets.EnsureBrowserExtension(supplied);
        Equal(Path.Combine(directory, "actual", "browser-extension"), exported);
        True(!Directory.Exists(Path.Combine(directory, "unused")), "Canonicalization must not create the discarded directory.");
        CheckBytes(exported);
        Sequence(new[] { "actual" }, Directory.GetDirectories(directory).Select(Path.GetFileName)!);
    });

    Run("a real data-directory junction cannot redirect export outside the requested path", () =>
    {
        var directory = NewDirectory("root-junction");
        var target = Path.Combine(directory, "physical-target");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep.txt"), "Synthetic junction target.");
        var alias = Path.Combine(directory, "data-alias");
        CreateJunction(alias, target);
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(alias));
        Sequence(before, Snapshot(directory));
        True(!Directory.Exists(Path.Combine(target, "browser-extension")), "Export escaped through a data-directory junction.");
    });

    Run("a real extension-directory junction cannot overwrite files in its target", () =>
    {
        var directory = NewDirectory("extension-junction");
        var data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
        var target = Path.Combine(directory, "outside-data"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "background.js"), "Must never be replaced through a junction.");
        CreateJunction(Path.Combine(data, "browser-extension"), target);
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(data));
        Sequence(before, Snapshot(directory));
    });

    Run("a junction ancestor is rejected even when descendant directories do not exist", () =>
    {
        var directory = NewDirectory("ancestor-junction");
        var target = Path.Combine(directory, "physical-target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "Do not create any descendants here.");
        var alias = Path.Combine(directory, "parent-alias"); CreateJunction(alias, target);
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(Path.Combine(alias, "missing", "data")));
        Sequence(before, Snapshot(directory));
    });

    Run("a junction occupying a required file blocks all earlier resource updates", () =>
    {
        var directory = NewDirectory("resource-junction");
        var data = Path.Combine(directory, "data");
        var exported = PackagedAssets.EnsureBrowserExtension(data);
        File.WriteAllText(Path.Combine(exported, "background.js"), "Keep the first file unchanged when validation fails.");
        var target = Path.Combine(directory, "physical-target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "Synthetic required-file junction target.");
        File.Delete(Path.Combine(exported, "rules.js")); CreateJunction(Path.Combine(exported, "rules.js"), target);
        var before = Snapshot(directory);
        Throws<IOException>(() => PackagedAssets.EnsureBrowserExtension(data));
        Sequence(before, Snapshot(directory));
    });
}
finally
{
    Run("guarded fixture cleanup unlinks junctions before deleting this run's temporary files", () =>
    {
        RequireScratchPath(scratch, allowRoot: true);
        foreach (var junction in junctions)
        {
            RequireScratchPath(junction);
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
        }
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    });
}

int passed = results.Count(result => result.Passed), failed = results.Count - passed;
Console.WriteLine($"Packaging tests: {passed}/{results.Count} passed.");
if (outputDirectory is not null)
{
    Directory.CreateDirectory(outputDirectory);
    File.WriteAllText(Path.Combine(outputDirectory, "packaging-tests.json"), JsonSerializer.Serialize(new { passed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
}
return failed == 0 ? 0 : 1;

void Run(string name, Action action)
{
    var watch = Stopwatch.StartNew();
    try { action(); results.Add(new(name, true, watch.ElapsedMilliseconds, null)); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add(new(name, false, watch.ElapsedMilliseconds, ex.ToString())); Console.WriteLine("FAIL " + name + ": " + ex); }
}

string NewDirectory(string name)
{
    var path = Path.Combine(scratch, name); RequireScratchPath(path); Directory.CreateDirectory(path); return path;
}

void RequireScratchPath(string path, bool allowRoot = false)
{
    var absolute = Path.GetFullPath(path);
    bool rootIsSafe = Directory.GetParent(scratch)?.FullName == tempParent && Path.GetFileName(scratch).StartsWith("FocusPlanet-Packaging-", StringComparison.Ordinal);
    if (!rootIsSafe || !(allowRoot && absolute.Equals(scratch, StringComparison.OrdinalIgnoreCase)) && !absolute.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing a test operation outside this run's temporary directory.");
}

void CheckBytes(string directory)
{
    foreach (string name in names)
        True(File.ReadAllBytes(Path.Combine(directory, name)).AsSpan().SequenceEqual(expected[name]), "Export differs from the source bytes: " + name);
}

static byte[] ExpectedBytes(string name)
{
    using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("FocusGarden.Packaging.Expected." + name)
        ?? throw new InvalidOperationException("Test source fixture resource is missing: " + name);
    using var content = new MemoryStream(); source.CopyTo(content); return content.ToArray();
}

void AddSyntheticPersonalFiles(string directory)
{
    foreach (var name in new[] { "focus.db", "planet.db", "planet-layout.db", "pairing.key", "settings.json", "backups/synthetic-old.sqlite", "token-logs/synthetic.jsonl" })
    {
        var path = Path.Combine(directory, name); RequireScratchPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("SYNTHETIC TEST SENTINEL: " + name));
    }
}

string[] Snapshot(string directory, string? excludedTopDirectory = null)
{
    var entries = new List<string>(); Walk(directory); return entries.Order(StringComparer.Ordinal).ToArray();
    void Walk(string current)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(current))
        {
            var relative = Path.GetRelativePath(directory, path);
            if (relative.Equals(excludedTopDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            // Do not traverse links even inside a synthetic fixture.
            if ((attributes & FileAttributes.ReparsePoint) != 0) { entries.Add("LINK " + relative); continue; }
            if ((attributes & FileAttributes.Directory) != 0) { entries.Add("DIR " + relative); Walk(path); }
            else entries.Add("FILE " + relative + " " + File.GetLastWriteTimeUtc(path).Ticks + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }
}

void CreateJunction(string alias, string target)
{
    RequireScratchPath(alias); RequireScratchPath(target); junctions.Add(alias);
    var start = new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
    start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:FOCUS_PACKAGING_TEST_ALIAS -Target $env:FOCUS_PACKAGING_TEST_TARGET | Out-Null");
    start.Environment["FOCUS_PACKAGING_TEST_ALIAS"] = alias; start.Environment["FOCUS_PACKAGING_TEST_TARGET"] = target;
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create the isolated junction fixture.");
    if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Junction fixture creation timed out."); }
    if (process.ExitCode != 0) throw new InvalidOperationException("Junction fixture failed: " + process.StandardError.ReadToEnd());
    True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "The fixture must be a real Windows junction.");
}

static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expectedValue, T actualValue) => True(EqualityComparer<T>.Default.Equals(expectedValue, actualValue), $"Expected {expectedValue}, got {actualValue}.");
static void Sequence<T>(IEnumerable<T> expectedValues, IEnumerable<T> actualValues) => True(expectedValues.SequenceEqual(actualValues), "Sequences differ; a file, byte hash, timestamp or path was changed unexpectedly.");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
}

internal sealed record TestResult(string Name, bool Passed, long Milliseconds, string? Error);
