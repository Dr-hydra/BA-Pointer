using System.Net.Http.Headers;
using System.Text.Json;

namespace BA.Pointer.Services;

public sealed record UpdateInfo(Version Version, string TagName, string ReleasePageUrl);

public sealed class UpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Dr-hydra/BA-Pointer/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await Client.GetAsync(
                LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                ErrorLog.WriteWarning("Update", $"GitHub release check returned {(int)response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;

            var tagName = root.GetProperty("tag_name").GetString()?.Trim() ?? string.Empty;
            var versionText = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var latestVersion))
            {
                ErrorLog.WriteWarning("Update", $"GitHub release tag is not a version: {tagName}");
                return null;
            }

            var releaseUrl = root.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(releaseUrl)) return null;
            if (latestVersion <= currentVersion)
            {
                ErrorLog.WriteInfo("Update", $"Already up to date. current={currentVersion}, latest={latestVersion}");
                return null;
            }

            ErrorLog.WriteInfo("Update", $"Update available. current={currentVersion}, latest={latestVersion}");
            return new UpdateInfo(latestVersion, tagName, releaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorLog.WriteWarning("Update", "GitHub release check timed out.");
            return null;
        }
        catch (Exception exception)
        {
            ErrorLog.Write(exception, "Update.Check");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = System.Net.WebRequest.DefaultWebProxy,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        handler.DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials;
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BA.Pointer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
