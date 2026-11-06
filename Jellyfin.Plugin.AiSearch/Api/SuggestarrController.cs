using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Suggestarr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>
/// Controller for Suggestarr / Overseerr integration, providing feed endpoints
/// for external automated suggestion collectors and connection tests.
/// </summary>
[ApiController]
[Authorize]
[Route("AiSearch/Suggestarr")]
public class SuggestarrController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly UserRatingStore _ratingStore;
    private readonly SuggestarrClient _suggestarrClient;

    /// <summary>Initializes a new instance of the <see cref="SuggestarrController"/> class.</summary>
    public SuggestarrController(
        ILibraryManager libraryManager,
        UserRatingStore ratingStore,
        SuggestarrClient suggestarrClient)
    {
        _libraryManager = libraryManager;
        _ratingStore = ratingStore;
        _suggestarrClient = suggestarrClient;
    }

    /// <summary>Tests connectivity to the configured Suggestarr / Jellyseerr instance.</summary>
    [HttpPost("Test")]
    public async Task<ActionResult<object>> TestConnection(CancellationToken cancellationToken)
    {
        var (success, message) = await _suggestarrClient.TestConnectionAsync(cancellationToken);
        return Ok(new { success, message });
    }

    /// <summary>
    /// Returns a list of highly rated items (default >= 4 stars) formatted for Suggestarr list ingestion.
    /// </summary>
    /// <param name="userId">The user ID whose ratings to fetch.</param>
    /// <param name="minStars">Minimum rating threshold (1-5, default 4).</param>
    /// <param name="limit">Max items to return (default 50).</param>
    [HttpGet("Feed")]
    public ActionResult<object> GetSuggestarrFeed(
        [FromQuery] Guid userId,
        [FromQuery] int minStars = 4,
        [FromQuery] int limit = 50)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("userId parameter is required.");
        }

        var ratedItems = _ratingStore.GetTopRated(userId, minStars, limit);
        var feedItems = new List<object>();

        foreach (var entry in ratedItems)
        {
            if (Guid.TryParse(entry.ItemId, out var parsedId))
            {
                var item = _libraryManager.GetItemById(parsedId);
                if (item is not null)
                {
                    item.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
                    item.ProviderIds.TryGetValue("Imdb", out var imdbId);
                    feedItems.Add(new
                    {
                        itemId = entry.ItemId,
                        title = item.Name,
                        year = item.ProductionYear,
                        mediaType = item is Movie ? "movie" : "tv",
                        rating = entry.Stars,
                        ratedAt = entry.RatedAt,
                        tmdbId = int.TryParse(tmdbId, out var parsedTmdb) ? parsedTmdb : (int?)null,
                        imdbId = imdbId,
                        genres = item.Genres ?? Array.Empty<string>()
                    });
                }
            }
        }

        return Ok(new
        {
            userId = userId.ToString("N"),
            count = feedItems.Count,
            minStars = minStars,
            items = feedItems
        });
    }
}
