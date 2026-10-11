using System.Text.Json;

namespace DuoSeat;

public sealed class Config
{
    public string Station2User { get; set; } = "DuoSeat2";
    public string Station2Address { get; set; } = "127.0.0.2";
    public int Monitor1 { get; set; } = 0;
    public int Monitor2 { get; set; } = 1;
    public double MouseSpeed { get; set; } = 1.5;

    /// <summary>Raw-input device paths dropped on Workplace 1. (Unassigned "System" devices also behave as station 1.)</summary>
    public List<string> Station1Devices { get; set; } = new();

    /// <summary>Raw-input device paths dropped on Workplace 2.</summary>
    public List<string> Station2Devices { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoSeat", "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (c != null) return c;
            }
        }
        catch
        {
            // corrupt config: fall back to defaults
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // not fatal
        }
    }
}
