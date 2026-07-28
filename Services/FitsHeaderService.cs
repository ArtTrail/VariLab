using System;
using System.Collections.Generic;
using System.IO;
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
            files.AddRange(Directory.GetFiles(directory, ext));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files.ToArray();
    }
}
