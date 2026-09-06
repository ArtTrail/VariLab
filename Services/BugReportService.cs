using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

/// <summary>Posts a feedback submission to the same shared Cloudflare Worker StarFix and
/// TransitLab already use (app-feedback.mobs-sync-trigger.workers.dev), which creates the
/// actual GitHub Issue on VariLab's behalf. The real GitHub token lives only in that Worker's
/// Cloudflare secret store — never in this source, never in the shipped app. "VariLab" was
/// added to the Worker's own repo allowlist (CloudflareWorkers/starfix-feedback/src/index.js)
/// alongside StarFix/TransitLab for this to work — it rejects any repo name not on that list.
///
/// ClientToken below is NOT a real secret — it's shipped in this binary like any other
/// string and a determined actor can extract it. Its only job is filtering out casual
/// discovery of the Worker's public URL. Worst case if it leaks: someone spams Issues on
/// an allowlisted repo — annoying, but the real GitHub token never leaves the Worker either
/// way.</summary>
public static class BugReportService
{
    private const string WorkerUrl = "https://app-feedback.mobs-sync-trigger.workers.dev";
    private const string RepoName = "VariLab";
    private const string ClientToken = "vX0lTWqUQeQRkAgGna3Ux9ufCXtN816nKuvxPOlGPIM";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SubmitAsync(
        string type, string summary, string description,
        string email, string version, string os,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            repo = RepoName,
            type,
            summary,
            description,
            email,
            version,
            os,
        });

        var req = new HttpRequestMessage(HttpMethod.Post, WorkerUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Client-Token", ClientToken);

        var response = await Http.SendAsync(req, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string reason;
            try
            {
                using var doc = JsonDocument.Parse(body);
                reason = doc.RootElement.TryGetProperty("error", out var errProp)
                    ? errProp.GetString() ?? body
                    : body;
            }
            catch (JsonException)
            {
                reason = body;
            }
            throw new HttpRequestException(reason);
        }
    }
}
