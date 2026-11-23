using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PortPilot.Core.Settings;

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// Persists settings as human-readable JSON. Writes are atomic so a crash never leaves a truncated file.
/// </summary>
public sealed class JsonSettingsStore(string path, ILogger<JsonSettingsStore>? logger = null) : ISettingsStore
{
    // Source-generated, so settings load without reflection (required for the native AOT Linux builds).
    private static readonly JsonSerializerOptions Options = new(SettingsJsonContext.Default.Options)
    {
        Converters = { new JsonStringEnumConverter<ThemePreference>(JsonNamingPolicy.CamelCase), new JsonStringEnumConverter<SortColumn>(JsonNamingPolicy.CamelCase), new JsonStringEnumConverter<Discovery.ProtocolFilter>(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = SettingsJsonContext.Default,
    };

    private static readonly JsonTypeInfo<AppSettings> TypeInfo = (JsonTypeInfo<AppSettings>)Options.GetTypeInfo(typeof(AppSettings));

    private readonly ILogger _log = logger ?? NullLogger<JsonSettingsStore>.Instance;
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortPilot", "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(Path);
                return (JsonSerializer.Deserialize(json, TypeInfo) ?? new AppSettings()).Normalize();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not read settings from {Path}; using defaults", Path);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, TypeInfo));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not save settings to {Path}", Path);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
