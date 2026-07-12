using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EldenRingEnableGraces;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--selftest" or "/selftest":
                    RunSelfTest();
                    return;
                case "--read" or "/read" when args.Length > 1:
                    RunRead(args[1]);
                    return;
                case "--roundtrip" or "/roundtrip" when args.Length > 1:
                    RunRoundtrip(args[1]);
                    return;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    /// <summary>
    /// Headless check (no GUI) that the embedded PARAMDEF and row-name metadata
    /// load and that the field we care about exists.
    /// </summary>
    private static void RunSelfTest()
    {
        try
        {
            var (paramdefFields, nameCount, hasEventFlag) = RegulationReader.SelfTest();
            Console.WriteLine("Self-test OK.");
            Console.WriteLine($"  Embedded PARAMDEF fields : {paramdefFields}");
            Console.WriteLine($"  Embedded row names       : {nameCount}");
            Console.WriteLine($"  'eventflagId' in PARAMDEF: {hasEventFlag}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Self-test FAILED: {e}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Headless read of a real regulation.bin: prints total row count and the
    /// first few graces.
    /// </summary>
    private static void RunRead(string regulationPath)
    {
        try
        {
            var rows = RegulationReader.ReadBonfireWarpParam(regulationPath);
            Console.WriteLine($"Read {rows.Count} BonfireWarpParam rows from {regulationPath}");
            Console.WriteLine();
            Console.WriteLine("  ID         Flag      Orig      State  Name");
            foreach (GraceRow r in rows.Take(15))
                Console.WriteLine(
                    $"  {r.Id,-10} {r.CurrentEventFlagId,-9} {r.OriginalEventFlagId,-9} " +
                    $"{(r.IsEnabled ? "[ON] " : "     ")}{r.Name}");
            if (rows.Count > 15)
                Console.WriteLine($"  … ({rows.Count - 15} more)");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Read FAILED: {e}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Proves the full read → edit → encrypt → write → re-read round-trip on a
    /// COPY of the regulation (the original is never touched). Enables a handful
    /// of rows, saves, re-reads, and verifies those rows are 71801 while every
    /// other row kept its original flag.
    /// </summary>
    private static void RunRoundtrip(string regulationPath)
    {
        string dir = Path.Combine(Path.GetTempPath(), "er-graces-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string work = Path.Combine(dir, "regulation.bin");
        File.Copy(regulationPath, work);

        try
        {
            Console.WriteLine($"Round-trip test on copy: {work}");
            var rows = RegulationReader.ReadBonfireWarpParam(work).ToList();
            var originalFlags = rows.ToDictionary(r => r.Id, r => r.CurrentEventFlagId);

            // Enable the first 5 rows whose flag isn't already 71801.
            var toEnable = rows
                .Where(r => r.CurrentEventFlagId != GraceRow.EnableEventFlagId)
                .Take(5)
                .Select(r => r.Id)
                .ToHashSet();

            Console.WriteLine($"  Enabling {toEnable.Count} row(s): {string.Join(", ", toEnable)}");
            foreach (GraceRow r in rows)
                if (toEnable.Contains(r.Id))
                    r.CurrentEventFlagId = GraceRow.EnableEventFlagId;

            RegulationReader.WriteBonfireWarpParam(work, rows);

            // Re-read and verify.
            var rows2 = RegulationReader.ReadBonfireWarpParam(work).ToDictionary(r => r.Id);

            int enabledOk = toEnable.Count(id =>
                rows2.TryGetValue(id, out GraceRow? r) && r.CurrentEventFlagId == GraceRow.EnableEventFlagId);
            int unchangedOk = rows2.Values.Count(r =>
                !toEnable.Contains(r.Id) && r.CurrentEventFlagId == originalFlags[r.Id]);

            Console.WriteLine();
            Console.WriteLine($"  Rows enabled to 71801 and verified : {enabledOk}/{toEnable.Count}");
            Console.WriteLine($"  Untouched rows still at original   : {unchangedOk}/{rows2.Count - toEnable.Count}");

            bool pass = enabledOk == toEnable.Count && unchangedOk == rows2.Count - toEnable.Count;
            Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
            if (!pass)
                Environment.Exit(1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* leave for inspection */ }
        }
    }
}
