using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

/// <summary>
/// Resolves a target star/object name to sky coordinates by querying, in order:
/// AAVSO VSX (variable star index — the most likely match for VariLab's use case),
/// then NASA Exoplanet Archive (host star or planet name), then SIMBAD (general
/// astronomical object database, via its identifier-alias table so any known name
/// works, not just the object's primary designation).
/// </summary>
public static class TargetResolverService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public record ResolveResult(double Ra, double Dec, string Source, string MatchedName);

    public static async Task<ResolveResult?> ResolveAsync(
        string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        progress?.Report("⟳  Trying AAVSO VSX…");
        var vsx = await TryVsxAsync(name, ct);
        if (vsx is not null) return vsx;

        progress?.Report("⟳  Not in VSX — trying NASA Exoplanet Archive…");
        var nea = await TryNeaAsync(name, ct);
        if (nea is not null) return nea;

        progress?.Report("⟳  Not in NASA Exoplanet Archive — trying SIMBAD…");
        var simbad = await TrySimbadAsync(name, ct);
        if (simbad is not null) return simbad;

        return null;
    }

    private static async Task<ResolveResult?> TryVsxAsync(string name, CancellationToken ct)
    {
        try
        {
            string url = "https://www.aavso.org/vsx/index.php?view=api.object" +
                         $"&ident={Uri.EscapeDataString(name)}&format=json";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("VSXObject", out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
                return null;

            if (!obj.TryGetProperty("RA2000", out var raEl) ||
                !obj.TryGetProperty("Declination2000", out var decEl))
                return null;

            if (!double.TryParse(raEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var ra))
                return null;
            if (!double.TryParse(decEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec))
                return null;

            string matched = obj.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? name : name;
            return new ResolveResult(ra, dec, "VSX", matched);
        }
        catch { return null; }
    }

    private static async Task<ResolveResult?> TryNeaAsync(string name, CancellationToken ct)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            string adql = $"SELECT pl_name,hostname,ra,dec FROM pscomppars " +
                          $"WHERE hostname = '{escaped}' OR pl_name = '{escaped}'";
            string url = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync?" +
                         $"query={Uri.EscapeDataString(adql)}&format=json";

            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var first = doc.RootElement[0];
            double ra  = first.GetProperty("ra").GetDouble();
            double dec = first.GetProperty("dec").GetDouble();
            string matched = first.TryGetProperty("pl_name", out var pn) ? pn.GetString() ?? name : name;
            return new ResolveResult(ra, dec, "NEA", matched);
        }
        catch { return null; }
    }

    private static async Task<ResolveResult?> TrySimbadAsync(string name, CancellationToken ct)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            string adql = "SELECT basic.main_id, basic.ra, basic.dec FROM basic " +
                          "JOIN ident ON ident.oidref = basic.oid " +
                          $"WHERE ident.id = '{escaped}'";
            string url = "https://simbad.u-strasbg.fr/simbad/sim-tap/sync?" +
                         $"request=doQuery&lang=adql&format=json&query={Uri.EscapeDataString(adql)}";

            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;

            var row = data[0];
            string matched = row[0].GetString() ?? name;
            double ra      = row[1].GetDouble();
            double dec     = row[2].GetDouble();
            return new ResolveResult(ra, dec, "SIMBAD", matched);
        }
        catch { return null; }
    }
}
