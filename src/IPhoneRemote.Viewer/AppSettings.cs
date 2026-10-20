using System.IO;
using System.Text.Json;

namespace IPhoneRemote.Viewer;

public enum StreamQuality { Smooth, Sharp, Saver }

public sealed record StreamProfile(int Framerate, int ScalingFactor, int JpegQuality)
{
    public static StreamProfile For(StreamQuality q) => q switch
    {
        StreamQuality.Sharp => new(24, 100, 80), // full resolution, a little slower
        StreamQuality.Saver => new(15, 40, 50),  // less CPU and battery on both ends
        _ => new(30, 50, 60),                    // default: lowest latency
    };
}

/// <summary>Per-user preferences in %APPDATA%\iPhoneRemote\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iPhoneRemote", "settings.json");

    public StreamQuality Quality { get; set; } = StreamQuality.Smooth;
    public bool ShowTouches { get; set; } = true;
    public bool Topmost { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? WdaBundleId { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
