using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Suggestarr;

/// <summary>
/// Communicates with Suggestarr (and Jellyseerr / Overseerr) APIs using official endpoints
/// to submit suggestions, automate download requests based on 1-5 star user ratings,
/// and verify connectivity.
/// </summary>
public sealed class SuggestarrClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SuggestarrClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="SuggestarrClient"/> class.</summary>
    public SuggestarrClient(IHttpClientFactory httpClientFactory, ILogger<SuggestarrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Tests connectivity to the configured Suggestarr / Jellyseerr endpoint.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return (false, "Plugin configuration unavailable.");
        }

        var baseUrl = config.SuggestarrUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, "Suggestarr URL is not configured.");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            // Try Suggestarr v1 openapi or docs / Jellyseerr status
            var targetUrl = config.SuggestarrProviderType?.ToLowerInvariant() == "jellyseerr"
                ? $"{baseUrl}/api/v1/status"
                : $"{baseUrl}/api/v1/openapi.json";

            using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            if (!string.IsNullOrWhiteSpace(config.SuggestarrApiKey))
            {
                req.Headers.Add("X-Api-Key", config.SuggestarrApiKey.Trim());
            }

            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return (true, $"Connected successfully to {config.SuggestarrProviderType} at {baseUrl} (Status: {(int)resp.StatusCode}).");
            }

            // Fallback check to root or docs
            using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/docs");
            using var fallbackResp = await client.SendAsync(fallbackReq, cancellationToken).ConfigureAwait(false);
            if (fallbackResp.IsSuccessStatusCode)
            {
                return (true, $"Connected to {baseUrl} (Docs reachable).");
            }

            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {targetUrl}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: Suggestarr connection test failed for {Url}", baseUrl);
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers automated suggestion/download processing when a user gives 4-5 stars to an item.
    /// </summary>
    public async Task OnItemRatedHighlyAsync(Guid userId, BaseItem item, int stars, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.SuggestarrEnabled)
        {
            return;
        }

        var baseUrl = config.SuggestarrUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        item.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
        item.ProviderIds.TryGetValue("Imdb", out var imdbId);
        var isMovie = item is Movie;
        var mediaType = isMovie ? "movie" : "tv";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            if (string.Equals(config.SuggestarrProviderType, "jellyseerr", StringComparison.OrdinalIgnoreCase))
            {
                // Native Jellyseerr / Overseerr request API
                if (int.TryParse(tmdbId, out var id))
                {
                    var payload = new
                    {
                        mediaType = mediaType,
                        mediaId = id
                    };

                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/request")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
                    };

                    if (!string.IsNullOrWhiteSpace(config.SuggestarrApiKey))
                    {
                        req.Headers.Add("X-Api-Key", config.SuggestarrApiKey.Trim());
                    }

                    using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "AiSearch: Jellyseerr request for {Item} (TMDb {Id}) returned HTTP {Status}",
                        item.Name,
                        id,
                        (int)resp.StatusCode);
                }
            }
            else
            {
                // Official Suggestarr v1 API
                var payload = new
                {
                    title = item.Name,
                    year = item.ProductionYear,
                    mediaType = mediaType,
                    tmdbId = int.TryParse(tmdbId, out var parsedTmdb) ? parsedTmdb : (int?)null,
                    imdbId = imdbId,
                    rating = stars,
                    userId = userId.ToString("N")
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/suggestions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(config.SuggestarrApiKey))
                {
                    req.Headers.Add("X-Api-Key", config.SuggestarrApiKey.Trim());
                }

                using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "AiSearch: Suggestarr suggestion notification for {Item} returned HTTP {Status}",
                    item.Name,
                    (int)resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: failed to notify Suggestarr/Jellyseerr for {Item}", item.Name);
        }
    }
}
