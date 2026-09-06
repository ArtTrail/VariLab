using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

public record UpdateInfo(string Version, string AssetName, string DownloadUrl);

/// <summary>Checks GitHub Releases for a newer VariLab version — same pattern as TransitLab's
/// own UpdateService, minus the Windows Inno-installer self-update path (VariLab currently
/// ships as plain per-platform zip/dmg archives on all three platforms, no installer yet).</summary>
public static class UpdateService
{
    private const string AllApiUrl = "https://api.github.com/repos/ArtTrail/VariLab/releases";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "VariLab-UpdateChecker" } },
    };

    // Matches a real VariLab version tag ("v1.5.1" or "1.5.1") — guards against this repo ever
    // hosting a non-app release tag the way TransitLab's repo does for unrelated data.
    private static readonly Regex AppVersionTagPattern = new(@"^v?\d+\.\d+\.\d+$", RegexOptions.Compiled);

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var root = await FindLatestAppReleaseAsync(ct);
            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null) return null;

            var latestVersion = tag.TrimStart('v');
            if (!IsNewer(latestVersion, currentVersion)) return null;

            var assets = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (MatchesThisPlatform(name))
                    return new UpdateInfo(latestVersion, name, url);
            }
        }
        catch { }
        return null;
    }

    public static async Task DownloadFileAsync(
        string url, string dest, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write);
        var buf = new byte[81920];
        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }
    }

    private static async Task<JsonNode?> FindLatestAppReleaseAsync(CancellationToken ct)
    {
        var json  = await Http.GetStringAsync(AllApiUrl, ct);
        var array = JsonNode.Parse(json)?.AsArray();
        if (array is null) return null;

        foreach (var release in array)
        {
            var tag = release?["tag_name"]?.GetValue<string>();
            if (tag is not null && AppVersionTagPattern.IsMatch(tag))
                return release;
        }
        return null;
    }

    /// <summary>VariLab's actual release asset names don't follow one consistent
    /// "-platform-" convention across platforms (e.g. "VariLab_v1.2.1_Windows.zip" vs.
    /// "VariLab-v1.2.1-osx-arm64.dmg" / "-linux-x64.zip") — matched directly against what's
    /// actually published rather than assuming TransitLab's win-x64/osx-arm64/linux-x64 scheme.</summary>
    private static bool MatchesThisPlatform(string assetName)
    {
        if (OperatingSystem.IsWindows())
            return assetName.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                   assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsMacOS())
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return assetName.Contains(arch, StringComparison.OrdinalIgnoreCase) &&
                   assetName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase);
        }

        return assetName.Contains("linux-x64", StringComparison.OrdinalIgnoreCase) &&
               assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest,  out var l)) return false;
        if (!Version.TryParse(current, out var c)) return false;
        return l > c;
    }
}
