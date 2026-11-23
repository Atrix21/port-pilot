using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace PortPilot.Linux.Services;

/// <summary>
/// Finds an application's icon the way a Linux desktop does: match the executable against the
/// <c>Exec=</c> line of installed <c>.desktop</c> entries, then look the <c>Icon=</c> name up in the icon
/// themes. Only PNGs are used (no SVG renderer is bundled). The index is built once, off the UI thread.
/// </summary>
internal static class DesktopIcons
{
    private static readonly string[] Sizes = ["64x64", "48x48", "128x128", "96x96", "256x256", "32x32", "512x512"];
    private static readonly string[] Themes = ["hicolor", "Adwaita", "Yaru", "breeze", "Papirus"];

    private static readonly Lazy<Dictionary<string, string>> Index = new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, Bitmap?> Bitmaps = new(StringComparer.Ordinal);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> DataDirs
    {
        get
        {
            yield return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome ? dataHome : Path.Combine(Home, ".local/share");
            var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
            foreach (var dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return dir;
            }

            yield return "/var/lib/flatpak/exports/share";
            yield return Path.Combine(Home, ".local/share/flatpak/exports/share");
        }
    }

    /// <summary>Returns the icon for a process, or null. Blocks on first use while the index builds; call off the UI thread.</summary>
    public static Bitmap? Find(string? executablePath, string processName)
    {
        var key = executablePath is { Length: > 0 } ? Path.GetFileName(executablePath) : processName;
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return Bitmaps.GetOrAdd(key, static k =>
        {
            var index = Index.Value;
            var name = index.GetValueOrDefault(k) ?? index.GetValueOrDefault(k.ToLowerInvariant());
            var file = name is null ? null : ResolveIcon(name);
            if (file is null)
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, 64);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in DataDirs.Select(d => Path.Combine(d, "applications")).Append("/var/lib/snapd/desktop/applications").Distinct())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    Read(file, index);
                }
                catch (IOException)
                {
                }
            }
        }

        return index;
    }

    private static void Read(string file, Dictionary<string, string> index)
    {
        string? exec = null, icon = null, wmClass = null;
        var inEntry = false;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.AsSpan().Trim();
            if (line.StartsWith("["))
            {
                if (inEntry)
                {
                    break;
                }

                inEntry = line.SequenceEqual("[Desktop Entry]");
                continue;
            }

            if (!inEntry)
            {
                continue;
            }

            if (line.StartsWith("Exec="))
            {
                exec = line[5..].ToString();
            }
            else if (line.StartsWith("Icon="))
            {
                icon = line[5..].ToString();
            }
            else if (line.StartsWith("StartupWMClass="))
            {
                wmClass = line[15..].ToString();
            }
        }

        if (string.IsNullOrEmpty(icon))
        {
            return;
        }

        foreach (var key in Keys(exec, wmClass, Path.GetFileNameWithoutExtension(file)))
        {
            index.TryAdd(key, icon);
        }
    }

    private static IEnumerable<string> Keys(string? exec, string? wmClass, string desktopId)
    {
        if (ExecutableName(exec) is { } name)
        {
            yield return name;
        }

        if (!string.IsNullOrEmpty(wmClass))
        {
            yield return wmClass.ToLowerInvariant();
        }

        // "org.gnome.Nautilus" → "nautilus", "code" → "code".
        var last = desktopId[(desktopId.LastIndexOf('.') + 1)..];
        yield return last.ToLowerInvariant();
    }

    /// <summary>The program an <c>Exec=</c> line runs, skipping <c>env VAR=…</c> prefixes and quotes.</summary>
    private static string? ExecutableName(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec))
        {
            return null;
        }

        foreach (var token in exec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim('"', '\'');
            if (t == "env" || t.Contains('=', StringComparison.Ordinal) || t.StartsWith('-'))
            {
                continue;
            }

            return Path.GetFileName(t);
        }

        return null;
    }

    private static string? ResolveIcon(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(name) ? name : null;
        }

        var iconDirs = DataDirs.Select(d => Path.Combine(d, "icons")).Prepend(Path.Combine(Home, ".icons")).Where(Directory.Exists).ToList();
        foreach (var theme in Themes)
        {
            foreach (var size in Sizes)
            {
                foreach (var dir in iconDirs)
                {
                    var candidate = Path.Combine(dir, theme, size, "apps", name + ".png");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        foreach (var dir in DataDirs.Select(d => Path.Combine(d, "pixmaps")).Append("/usr/share/pixmaps"))
        {
            var candidate = Path.Combine(dir, name + ".png");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
