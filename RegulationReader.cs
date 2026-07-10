using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Andre.Formats;   // Param (Smithbox's faster, version-aware PARAM impl)
using SoulsFormats;    // BND4, PARAMDEF, SFUtil, BinderFile

namespace EldenRingEnableGraces;

/// <summary>
/// Reads/writes Elden Ring's <c>regulation.bin</c>, focused on
/// <c>BonfireWarpParam</c>. Uses Smithbox's SoulsFormats fork (to decrypt and
/// re-encrypt the regulation) and its <c>Andre.Formats.Param</c> (to read/write
/// the param — its write path actually works, unlike raw SoulsFormats.PARAM for
/// this paramdef).
/// </summary>
public static class RegulationReader
{
    private const string ParamName = "BonfireWarpParam";
    private const string EventFlagField = "eventflagId";

    /// <summary>Zero IV, matching Smithbox's ER regulation encryption.</summary>
    private static readonly byte[] ZeroIv = new byte[16];

    // ---------------------------------------------------------------------
    // Read
    // ---------------------------------------------------------------------

    /// <summary>
    /// Load and decrypt <paramref name="regulationBinPath"/>, returning every
    /// BonfireWarpParam row with its English name merged in. Both
    /// <see cref="GraceRow.CurrentEventFlagId"/> and
    /// <see cref="GraceRow.OriginalEventFlagId"/> are set to the loaded value;
    /// the caller overlays the persisted sidecar originals on top.
    /// </summary>
    public static IReadOnlyList<GraceRow> ReadBonfireWarpParam(string regulationBinPath)
    {
        byte[] data = File.ReadAllBytes(regulationBinPath);

        // DecryptERRegulation handles DCX/DCP decompression + AES automatically,
        // and gracefully no-ops if the file is already a plain BND4.
        using BND4 bnd = SFUtil.DecryptERRegulation(data);

        BinderFile? paramFile = FindBonfireWarpParamFile(bnd)
            ?? throw new InvalidDataException(
                $"{ParamName}.param was not found inside regulation.bin. " +
                "Is this actually an Elden Ring regulation file?");

        PARAMDEF paramdef = LoadEmbeddedParamdef();
        (Param param, Param.Column? flagCol) = LoadParam(bnd, paramFile, paramdef);

        Dictionary<int, string> names = LoadRowNames();

        var rows = new List<GraceRow>(param.Rows.Count);
        foreach (Param.Row row in param.Rows)
        {
            string name = names.TryGetValue(row.ID, out string? named)
                ? named
                : (row.Name ?? string.Empty);

            uint flag = flagCol is null
                ? 0u
                : Convert.ToUInt32(flagCol.GetValue(row));

            rows.Add(new GraceRow
            {
                Id = row.ID,
                Name = name,
                CurrentEventFlagId = flag,
                OriginalEventFlagId = flag,
            });
        }

        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        return rows;
    }

    // ---------------------------------------------------------------------
    // Write
    // ---------------------------------------------------------------------

    /// <summary>
    /// Write the given rows' <see cref="GraceRow.CurrentEventFlagId"/> back into
    /// <paramref name="regulationBinPath"/>. Creates a one-time <c>.bak</c> backup
    /// of the original file, re-encrypts with a zero IV (mirroring Smithbox's ER
    /// save path), and swaps via a <c>.temp</c> file. All params other than
    /// BonfireWarpParam are carried through untouched.
    /// </summary>
    public static void WriteBonfireWarpParam(string regulationBinPath, IEnumerable<GraceRow> rows)
    {
        // One-time byte backup of the pristine file.
        string bakPath = GetBackupPath(regulationBinPath);
        if (!File.Exists(bakPath))
            File.Copy(regulationBinPath, bakPath, overwrite: false);

        byte[] data = File.ReadAllBytes(regulationBinPath);
        using BND4 bnd = SFUtil.DecryptERRegulation(data);

        BinderFile? paramFile = FindBonfireWarpParamFile(bnd)
            ?? throw new InvalidDataException($"{ParamName}.param not found in regulation.bin.");

        PARAMDEF paramdef = LoadEmbeddedParamdef();
        (Param param, Param.Column? flagCol) = LoadParam(bnd, paramFile, paramdef);

        if (flagCol is null)
            throw new InvalidDataException("'eventflagId' column not found in BonfireWarpParam paramdef.");

        Dictionary<int, GraceRow> byId = rows.ToDictionary(r => r.Id);
        foreach (Param.Row row in param.Rows)
        {
            if (byId.TryGetValue(row.ID, out GraceRow? gr))
                flagCol.SetValue(row, gr.CurrentEventFlagId);
        }

        paramFile.Bytes = param.Write();

        // Encrypt with a zero IV (no DCX) — the form the game accepts.
        byte[] output = SFUtil.EncryptERRegulation(bnd, ZeroIv);

        string tempPath = regulationBinPath + ".temp";
        File.WriteAllBytes(tempPath, output);
        File.Move(tempPath, regulationBinPath, overwrite: true);
    }

    // ---------------------------------------------------------------------
    // Sidecar (originals) + backup paths
    // ---------------------------------------------------------------------

    public static string GetOriginalsPath(string regulationBinPath) => regulationBinPath + ".originals.json";
    public static string GetBackupPath(string regulationBinPath) => regulationBinPath + ".bak";

    /// <summary>Load the persisted row-id → original-flag map, or an empty map if absent.</summary>
    public static Dictionary<int, uint> LoadOriginals(string sidecarPath)
    {
        var dict = new Dictionary<int, uint>();
        if (!File.Exists(sidecarPath))
            return dict;

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (int.TryParse(prop.Name, out int id))
                dict[id] = prop.Value.GetUInt32();
        }
        return dict;
    }

    /// <summary>Persist the row-id → original-flag map.</summary>
    public static void SaveOriginals(string sidecarPath, IReadOnlyDictionary<int, uint> originals)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(originals, options);
        File.WriteAllText(sidecarPath, json);
    }

    // ---------------------------------------------------------------------
    // Self-test (headless, no filesystem beyond the embedded assets)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Headless smoke test: load the embedded PARAMDEF + row names and confirm
    /// the <c>eventflagId</c> field is defined.
    /// </summary>
    public static (int ParamdefFields, int NameCount, bool HasEventFlag) SelfTest()
    {
        PARAMDEF def = LoadEmbeddedParamdef();
        Dictionary<int, string> names = LoadRowNames();
        bool hasEventFlag = def.Fields.Any(f => f.InternalName == EventFlagField);
        return (def.Fields.Count, names.Count, hasEventFlag);
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private static BinderFile? FindBonfireWarpParamFile(BND4 bnd) => bnd.Files.FirstOrDefault(f =>
        string.Equals(
            Path.GetFileNameWithoutExtension(f.Name ?? string.Empty),
            ParamName,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Read a PARAM and apply the paramdef version-aware, using the regulation
    /// version carried by the BND4 header (falls back to "latest" if the version
    /// string isn't numeric). Returns the loaded param plus the eventflagId column.
    /// </summary>
    private static (Param param, Param.Column? flagCol) LoadParam(BND4 bnd, BinderFile file, PARAMDEF paramdef)
    {
        Param param = Param.Read(file.Bytes);
        ulong version = ulong.TryParse(bnd.Version, out ulong v) ? v : ulong.MaxValue;
        param.ApplyParamdef(paramdef, version);
        return (param, param[EventFlagField]);
    }

    private static PARAMDEF LoadEmbeddedParamdef()
    {
        // The shipped file is an *XML* PARAMDEF (as Smithbox stores under
        // Assets/PARAM/<game>/Defs/). PARAMDEF.Read() is the binary reader, so we
        // must use the XML deserializer. versionAware=true so fields gated by
        // FirstVersion/RemovedVersion resolve against the param's regulation version.
        byte[] bytes = ReadEmbeddedResource("BonfireWarpParam.xml");
        string tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            return PARAMDEF.XmlDeserialize(tempPath, versionAware: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static Dictionary<int, string> LoadRowNames()
    {
        byte[] bytes = ReadEmbeddedResource("BonfireWarpParam.json");
        using var doc = JsonDocument.Parse(bytes);

        var dict = new Dictionary<int, string>();
        foreach (JsonElement entry in doc.RootElement.GetProperty("Entries").EnumerateArray())
        {
            int id = entry.GetProperty("ID").GetInt32();
            JsonElement texts = entry.GetProperty("Entries");
            string text = texts.GetArrayLength() > 0
                ? texts[0].GetString() ?? string.Empty
                : string.Empty;
            dict[id] = text;
        }
        return dict;
    }

    private static byte[] ReadEmbeddedResource(string endsWith)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? fullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));

        if (fullName is null)
            throw new InvalidOperationException($"Embedded asset '{endsWith}' is missing.");

        using Stream stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Could not open embedded asset '{fullName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
