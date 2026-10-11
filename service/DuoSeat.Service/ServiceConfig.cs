using System.Text.Json;

namespace DuoSeat.Service;

internal sealed class ServiceConfig
{
    public int Monitor1 { get; set; } = 0;
    public int Monitor2 { get; set; } = 1;
    public double MouseSpeed { get; set; } = 1.5;

    /// <summary>VM mode: TCP port, listen address (empty = auto-detect the Hyper-V virtual switch) and shared secret.</summary>
    public int TcpPort { get; set; } = 47600;
    public string VmListenAddress { get; set; } = "";
    public string Token { get; set; } = "";

    public string EnsureToken()
    {
        if (string.IsNullOrEmpty(Token))
        {
            Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            Save();
        }
        return Token;
    }

    /// <summary>Windows user of station 2 (the only user whose agent may connect).</summary>
    public string Station2User { get; set; } = "DuoSeat2";

    /// <summary>After this many ms without station-2 input, keyboard focus returns to the station-1 window.</summary>
    public int FocusLeaseMs { get; set; } = 400;

    /// <summary>PnP instance ids (as printed by "list") of the devices that belong to station 2.</summary>
    public List<string> Station2Devices { get; set; } = new();

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DuoSeat", "service.json");

    public bool IsStation2(string instanceId) =>
        Station2Devices.Any(d => string.Equals(d, instanceId, StringComparison.OrdinalIgnoreCase));

    public static ServiceConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(FilePath)) ?? new ServiceConfig();
        }
        catch
        {
            // corrupt file: use defaults
        }
        return new ServiceConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
