using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaTagger.Services
{
    public sealed record UpdateCheckResult(
        bool UpdateAvailable,
        string? LatestVersion,
        string? ReleaseUrl,
        string? ErrorMessage);

    /// <summary>
    /// Checks GitHub Releases for a newer TagCat. Notify-only by design: it reads the latest
    /// release's tag and hands back a URL, and never downloads or runs anything. That keeps
    /// the whole feature free of the trust and rollback problems that come with an app that
    /// can replace its own executable.
    ///
    /// Reads from the public Releases API, so no token or account is involved. Note this
    /// finds releases only - an installer committed directly into the repository as an
    /// ordinary file is invisible here, because it isn't a release.
    /// </summary>
    public static class UpdateChecker
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/DanBaaz/TagCat/releases/latest";

        public const string ReleasesPageUrl = "https://github.com/DanBaaz/TagCat/releases";

        /// <summary>Shared deliberately: HttpClient is designed to be reused, and creating one
        /// per call is a well-known way to exhaust sockets.</summary>
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub rejects requests with no User-Agent outright, so this is required rather
            // than merely polite.
            client.DefaultRequestHeaders.Add("User-Agent", "TagCat-UpdateCheck");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        /// <summary>
        /// Never throws. A failed check returns an ErrorMessage instead, because being unable
        /// to reach GitHub is an ordinary condition (offline, firewall, rate limit) and should
        /// never disrupt the app.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(
            string currentVersion, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = await Http.GetStringAsync(LatestReleaseApi, cancellationToken)
                    .ConfigureAwait(false);

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var tag = root.TryGetProperty("tag_name", out var tagElement)
                    ? tagElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(tag))
                    return new UpdateCheckResult(false, null, null, "The latest release has no version tag.");

                var url = root.TryGetProperty("html_url", out var urlElement)
                    ? urlElement.GetString() ?? ReleasesPageUrl
                    : ReleasesPageUrl;

                var comparison = VersionComparer.Compare(tag, currentVersion);

                // Null means one of the two didn't parse. Reported rather than silently
                // treated as "up to date", which would hide a broken check indefinitely.
                if (comparison == null)
                    return new UpdateCheckResult(false, tag, url,
                        $"Couldn't compare the latest version ({tag}) with this one ({currentVersion}).");

                return new UpdateCheckResult(comparison > 0, tag, url, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return new UpdateCheckResult(false, null, null,
                    "Couldn't reach GitHub. Check your internet connection and try again.");
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult(false, null, null, ex.Message);
            }
        }
    }
}
