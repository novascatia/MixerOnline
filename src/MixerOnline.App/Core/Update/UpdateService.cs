using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MixerOnline.Core.Update
{
    /// <summary>
    /// Represents a parsed semantic version (e.g. 1.2.1, 1.2.1-beta, v1.0.0).
    /// </summary>
    public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public bool IsPreRelease { get; }
        public string PreReleaseTag { get; }

        public SemanticVersion(int major, int minor, int patch, string? preReleaseTag = null)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            IsPreRelease = !string.IsNullOrWhiteSpace(preReleaseTag);
            PreReleaseTag = preReleaseTag?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        public static bool TryParse(string? versionString, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(versionString)) return false;

            string clean = versionString.Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(1).Trim();
            }

            string preRelease = string.Empty;
            int dashIndex = clean.IndexOf('-');
            if (dashIndex >= 0)
            {
                preRelease = clean.Substring(dashIndex + 1);
                clean = clean.Substring(0, dashIndex);
            }

            string[] parts = clean.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;

            if (!int.TryParse(parts[0], out int major) ||
                !int.TryParse(parts[1], out int minor))
            {
                return false;
            }

            int patch = 0;
            if (parts.Length >= 3 && !int.TryParse(parts[2], out patch))
            {
                return false;
            }

            version = new SemanticVersion(major, minor, patch, preRelease);
            return true;
        }

        public static SemanticVersion Parse(string versionString)
        {
            if (TryParse(versionString, out var v)) return v;
            throw new FormatException($"Invalid semantic version string: '{versionString}'");
        }

        public int CompareTo(SemanticVersion other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

            // A release with NO prerelease tag is strictly newer than one with a prerelease tag (e.g. 1.2.1 > 1.2.1-beta)
            if (!IsPreRelease && other.IsPreRelease) return 1;
            if (IsPreRelease && !other.IsPreRelease) return -1;

            if (IsPreRelease && other.IsPreRelease)
            {
                return string.Compare(PreReleaseTag, other.PreReleaseTag, StringComparison.OrdinalIgnoreCase);
            }

            return 0;
        }

        public bool Equals(SemanticVersion other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, IsPreRelease, PreReleaseTag);

        public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
        public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
        public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
        public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
        public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

        public override string ToString()
        {
            return IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreReleaseTag}" : $"{Major}.{Minor}.{Patch}";
        }
    }

    /// <summary>
    /// Metadata describing a release retrieved from GitHub.
    /// </summary>
    public sealed class UpdateReleaseInfo
    {
        public string TagName { get; init; } = string.Empty;
        public SemanticVersion Version { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Changelog { get; init; } = string.Empty;
        public bool IsPreRelease { get; init; }
        public string DownloadUrl { get; init; } = string.Empty;
        public long DownloadSize { get; init; }
        public DateTime? PublishedAt { get; init; }
        public string HtmlUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Result of an update check.
    /// </summary>
    public sealed class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = string.Empty;
        public UpdateReleaseInfo? LatestRelease { get; init; }
        public string? Error { get; init; }

        public static UpdateCheckResult UpToDate(string currentVersion, UpdateReleaseInfo? latest) =>
            new() { IsUpdateAvailable = false, CurrentVersion = currentVersion, LatestRelease = latest };

        public static UpdateCheckResult Available(string currentVersion, UpdateReleaseInfo latest) =>
            new() { IsUpdateAvailable = true, CurrentVersion = currentVersion, LatestRelease = latest };

        public static UpdateCheckResult Failed(string currentVersion, string error) =>
            new() { IsUpdateAvailable = false, CurrentVersion = currentVersion, Error = error };
    }

    /// <summary>
    /// Service responsible for querying GitHub Releases, comparing versions,
    /// and downloading standalone update packages.
    /// </summary>
    public sealed class UpdateService
    {
        private const string GitHubApiReleasesUrl = "https://api.github.com/repos/novascatia/MixerOnline/releases";
        private const string UserAgentAppName = "MixerOnline-App";

        private readonly HttpClient _httpClient;

        // GitHub Release DTOs for JSON deserialization
        private sealed class GitHubReleaseDto
        {
            [JsonPropertyName("tag_name")]
            public string? TagName { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("body")]
            public string? Body { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime? PublishedAt { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }

            [JsonPropertyName("assets")]
            public List<GitHubAssetDto>? Assets { get; set; }
        }

        private sealed class GitHubAssetDto
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }
        }

        public UpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgentAppName, "1.2.1"));
            }
        }

        /// <summary>
        /// Checks GitHub Releases API for any newer version than currentVersion.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdatesAsync(
            string currentVersionString,
            bool includeBeta = true,
            CancellationToken cancellationToken = default)
        {
            if (!SemanticVersion.TryParse(currentVersionString, out var currentVersion))
            {
                return UpdateCheckResult.Failed(currentVersionString, $"Invalid current version format: '{currentVersionString}'");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiReleasesUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return UpdateCheckResult.Failed(currentVersionString, "GitHub API rate limit exceeded. Please try again later.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.Failed(currentVersionString, $"GitHub API returned error: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseDto>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (releases == null || releases.Count == 0)
                {
                    return UpdateCheckResult.UpToDate(currentVersionString, null);
                }

                UpdateReleaseInfo? bestRelease = null;

                foreach (var rel in releases)
                {
                    if (rel.Draft) continue;
                    if (!includeBeta && rel.Prerelease) continue;
                    if (string.IsNullOrWhiteSpace(rel.TagName)) continue;

                    if (!SemanticVersion.TryParse(rel.TagName, out var relVersion)) continue;

                    // Match asset: prefer zip archive for current channel
                    string downloadUrl = string.Empty;
                    long downloadSize = 0;

                    if (rel.Assets != null && rel.Assets.Count > 0)
                    {
                        GitHubAssetDto? bestAsset = null;

                        // Priority 1: Zip matching beta/stable channel
                        foreach (var asset in rel.Assets)
                        {
                            if (string.IsNullOrEmpty(asset.Name) || string.IsNullOrEmpty(asset.BrowserDownloadUrl)) continue;

                            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                bool isBetaAsset = asset.Name.Contains("beta", StringComparison.OrdinalIgnoreCase);
                                if (includeBeta && isBetaAsset)
                                {
                                    bestAsset = asset;
                                    break;
                                }
                                if (!includeBeta && !isBetaAsset)
                                {
                                    bestAsset = asset;
                                    break;
                                }
                                bestAsset ??= asset;
                            }
                        }

                        // Priority 2: Standalone .exe
                        if (bestAsset == null)
                        {
                            foreach (var asset in rel.Assets)
                            {
                                if (asset.Name != null && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    bestAsset = asset;
                                    break;
                                }
                            }
                        }

                        if (bestAsset != null)
                        {
                            downloadUrl = bestAsset.BrowserDownloadUrl ?? string.Empty;
                            downloadSize = bestAsset.Size;
                        }
                    }

                    var info = new UpdateReleaseInfo
                    {
                        TagName = rel.TagName,
                        Version = relVersion,
                        Title = !string.IsNullOrWhiteSpace(rel.Name) ? rel.Name : rel.TagName,
                        Changelog = rel.Body ?? string.Empty,
                        IsPreRelease = rel.Prerelease,
                        DownloadUrl = downloadUrl,
                        DownloadSize = downloadSize,
                        PublishedAt = rel.PublishedAt,
                        HtmlUrl = rel.HtmlUrl ?? $"https://github.com/novascatia/MixerOnline/releases/tag/{rel.TagName}"
                    };

                    if (bestRelease == null || info.Version > bestRelease.Version)
                    {
                        bestRelease = info;
                    }
                }

                if (bestRelease == null)
                {
                    return UpdateCheckResult.UpToDate(currentVersionString, null);
                }

                if (bestRelease.Version > currentVersion)
                {
                    return UpdateCheckResult.Available(currentVersionString, bestRelease);
                }

                return UpdateCheckResult.UpToDate(currentVersionString, bestRelease);
            }
            catch (OperationCanceledException)
            {
                return UpdateCheckResult.Failed(currentVersionString, "Update check was canceled.");
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failed(currentVersionString, $"Network error: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads the update file from downloadUrl to destinationPath with streaming progress reporting.
        /// </summary>
        public async Task DownloadUpdateAsync(
            string downloadUrl,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new ArgumentException("Download URL cannot be empty.", nameof(downloadUrl));
            }

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = (double)totalRead / totalBytes.Value;
                    progress?.Report(Math.Clamp(pct, 0.0, 1.0));
                }
            }

            progress?.Report(1.0);
        }
    }
}
