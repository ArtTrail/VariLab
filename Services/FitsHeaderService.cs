using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VariLab.Services;

/// <summary>
/// Reads keyword values from a FITS primary header without any external library.
/// FITS headers consist of 2880-byte blocks; each 80-byte record has the form:
///   KEYWORD = VALUE / comment
/// </summary>
public static class FitsHeaderService
{
    public class FitsHeader
    {
        private readonly Dictionary<string, string> _kv;
        public FitsHeader(Dictionary<string, string> kv) => _kv = kv;

        /// <summary>Return the raw string value for a keyword, or "" if absent.</summary>
        public string Get(string keyword)
            => _kv.TryGetValue(keyword.ToUpperInvariant().TrimEnd(), out var v) ? v : "";

        /// <summary>Return a double or null if absent / unparseable.</summary>
        public double? GetDouble(string keyword)
            => double.TryParse(Get(keyword), System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out var d)
               ? d : null;

        /// <summary>Return an int or null if absent / unparseable.</summary>
        public int? GetInt(string keyword)
            => int.TryParse(Get(keyword), out var i) ? i : null;
    }

    /// <summary>Read the primary header from a FITS file. Throws on I/O or format error.</summary>
    public static FitsHeader Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var block = new byte[2880];
        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 80) break;

            bool end = false;
            for (int i = 0; i + 79 < read; i += 80)
            {
                var record = Encoding.ASCII.GetString(block, i, 80);
                var kw     = record[..8].TrimEnd();

                if (kw == "END") { end = true; break; }

                // Only value records contain '=' at position 8
                if (record.Length > 9 && record[8] == '=')
                {
                    var rawVal = record[9..].Split('/')[0].Trim();

                    // Strip FITS string quotes
                    if (rawVal.StartsWith('\''))
                    {
                        rawVal = rawVal.Trim('\'').Trim();
                    }

                    kv[kw] = rawVal;
                }
            }
            if (end) break;
        }
        return new FitsHeader(kv);
    }

    /// <summary>Find the first FITS file in a directory (alphabetically, case-insensitive).</summary>
    public static string? FindFirstFits(string directory)
    {
        var all = FindAllFits(directory);
        return all.Length > 0 ? all[0] : null;
    }

    /// <summary>Find all FITS files in a directory, sorted alphabetically (case-insensitive).</summary>
    public static string[] FindAllFits(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var files = new List<string>();
        foreach (var ext in new[] { "*.fits", "*.fit", "*.fts" })
        {
            try
            {
                files.AddRange(Directory.GetFiles(directory, ext));
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // Some SMB servers (observed against macOS file sharing) corrupt the
                // batched FindFirstFileEx response .NET's directory enumerator uses once
                // a listing spans more than one network round-trip, surfacing here as
                // "Invalid Signature." rather than the real page-fetch failure. The
                // classic single-entry FindFirstFile/FindNextFile API (what cmd.exe's
                // `dir` uses) doesn't batch and has been confirmed to list the same
                // directories correctly regardless of size.
                files.AddRange(FindFilesLegacyWin32(directory, ext));
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static List<string> FindFilesLegacyWin32(string directory, string pattern)
    {
        var results = new List<string>();
        var handle = FindFirstFileW(Path.Combine(directory, pattern), out var data);
        if (handle == INVALID_HANDLE_VALUE) return results;
        try
        {
            do
            {
                if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
                    results.Add(Path.Combine(directory, data.cFileName));
            } while (FindNextFileW(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }
        return results;
    }

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public uint ftCreationTime_lo, ftCreationTime_hi;
        public uint ftLastAccessTime_lo, ftLastAccessTime_hi;
        public uint ftLastWriteTime_lo, ftLastWriteTime_hi;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);
}
