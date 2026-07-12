using System;
using System.IO;
using System.Text.Json;

namespace EldenRingEnableGraces;

/// <summary>
/// Tiny persisted settings store for the app. Currently just the configurable
/// "enable" event-flag id. Stored as JSON under %LocalAppData%/eldenring-enable-graces/.
/// </summary>
public static class AppSettings
{
    /// <summary>Default force-enable flag if no setting is stored.</summary>
    public const uint DefaultEnableFlag = 76800;

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "eldenring-enable-graces");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static uint LoadEnableFlag()
    {
        try
        {
            if (!File.Exists(FilePath))
                return DefaultEnableFlag;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            return doc.RootElement.TryGetProperty("EnableEventFlagId", out JsonElement el)
                && el.TryGetUInt32(out uint value)
                    ? value
                    : DefaultEnableFlag;
        }
        catch
        {
            return DefaultEnableFlag;
        }
    }

    public static void SaveEnableFlag(uint value)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(new { EnableEventFlagId = value }, options);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Settings are non-critical; never crash the UI over them.
        }
    }
}
