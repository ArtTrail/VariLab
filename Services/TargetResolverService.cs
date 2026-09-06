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
///
/// Every attempt, failure, and success is written to SessionLogService (the persistent
/// Diagnostics log), not just reported via the transient `progress` callback — a real gap
/// found 2026-09-04: AAVSO silently moved VSX from www.aavso.org/vsx/... to vsx.aavso.org
/// (the old URL now 301s through a path that trips Cloudflare's bot challenge for a plain
/// HttpClient — confirmed via direct testing, a User-Agent header alone didn't help), and
/// every failure mode here (bad status code, unexpected JSON shape, parse failure,
/// exception) was previously silent — visible only as "not found" in the transient status
/// text, with zero trace in the Diagnostics log to explain why. That made this exact class
/// of breakage (a third-party API silently changing) effectively undiagnosable after the
/// fact. Never go back to a bare `catch { return null; }` here.
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

        SessionLogService.Write($"[TargetResolver] '{name}' not resolved by VSX, NEA, or SIMBAD.");
        return null;
    }

    private static async Task<ResolveResult?> TryVsxAsync(string name, CancellationToken ct)
    {
        string url = "https://vsx.aavso.org/index.php?view=api.object" +
                     $"&ident={Uri.EscapeDataString(name)}&format=json";
        SessionLogService.Write($"[TargetResolver] VSX: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("VSXObject", out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
            {
                SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> no match (empty VSXObject). Raw: {Truncate(json)}");
                return null;
            }

            if (!obj.TryGetProperty("RA2000", out var raEl) ||
                !obj.TryGetProperty("Declination2000", out var decEl))
            {
                SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> matched but missing RA2000/Declination2000. Raw: {Truncate(json)}");
                return null;
            }

            if (!double.TryParse(raEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var ra))
            {
                SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> RA2000 '{raEl.GetString()}' didn't parse as a number.");
                return null;
            }
            if (!double.TryParse(decEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec))
            {
                SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> Declination2000 '{decEl.GetString()}' didn't parse as a number.");
                return null;
            }

            string matched = obj.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? name : name;
            SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> resolved as '{matched}', RA={ra:F6} Dec={dec:F6}");
            return new ResolveResult(ra, dec, "VSX", matched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SessionLogService.Write($"[TargetResolver] VSX: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ResolveResult?> TryNeaAsync(string name, CancellationToken ct)
    {
        string escaped = name.Replace("'", "''");
        string adql = $"SELECT pl_name,hostname,ra,dec FROM pscomppars " +
                      $"WHERE hostname = '{escaped}' OR pl_name = '{escaped}'";
        string url = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync?" +
                     $"query={Uri.EscapeDataString(adql)}&format=json";
        SessionLogService.Write($"[TargetResolver] NEA: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                SessionLogService.Write($"[TargetResolver] NEA: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                SessionLogService.Write($"[TargetResolver] NEA: '{name}' -> no match. Raw: {Truncate(json)}");
                return null;
            }

            var first = doc.RootElement[0];
            double ra  = first.GetProperty("ra").GetDouble();
            double dec = first.GetProperty("dec").GetDouble();
            string matched = first.TryGetProperty("pl_name", out var pn) ? pn.GetString() ?? name : name;
            SessionLogService.Write($"[TargetResolver] NEA: '{name}' -> resolved as '{matched}', RA={ra:F6} Dec={dec:F6}");
            return new ResolveResult(ra, dec, "NEA", matched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SessionLogService.Write($"[TargetResolver] NEA: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ResolveResult?> TrySimbadAsync(string name, CancellationToken ct)
    {
        string escaped = name.Replace("'", "''");
        string adql = "SELECT basic.main_id, basic.ra, basic.dec FROM basic " +
                      "JOIN ident ON ident.oidref = basic.oid " +
                      $"WHERE ident.id = '{escaped}'";
        string url = "https://simbad.u-strasbg.fr/simbad/sim-tap/sync?" +
                     $"request=doQuery&lang=adql&format=json&query={Uri.EscapeDataString(adql)}";
        SessionLogService.Write($"[TargetResolver] SIMBAD: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                SessionLogService.Write($"[TargetResolver] SIMBAD: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                SessionLogService.Write($"[TargetResolver] SIMBAD: '{name}' -> no match. Raw: {Truncate(json)}");
                return null;
            }

            var row = data[0];
            string matched = row[0].GetString() ?? name;
            double ra      = row[1].GetDouble();
            double dec     = row[2].GetDouble();
            SessionLogService.Write($"[TargetResolver] SIMBAD: '{name}' -> resolved as '{matched}', RA={ra:F6} Dec={dec:F6}");
            return new ResolveResult(ra, dec, "SIMBAD", matched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SessionLogService.Write($"[TargetResolver] SIMBAD: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";
}
