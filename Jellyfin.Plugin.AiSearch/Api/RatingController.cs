using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Suggestarr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>Request payload to rate a media item.</summary>
public sealed class SetRatingRequest
{
    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the rating in stars (1 to 5, or 0 to clear).</summary>
    public int Rating { get; set; }
}

/// <summary>
/// Provides official endpoints to submit and query Crunchyroll-style 1-5 star ratings.
/// Automatically synchronizes ratings with Jellyfin's native <see cref="IUserDataManager"/>
/// and feeds into <see cref="SuggestarrClient"/> auto-requests.
/// </summary>
[ApiController]
[Authorize]
[Route("AiSearch/Rating")]
public class RatingController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly UserRatingStore _ratingStore;
    private readonly SuggestarrClient _suggestarrClient;
    private readonly ILogger<RatingController> _logger;

    /// <summary>Initializes a new instance of the <see cref="RatingController"/> class.</summary>
    public RatingController(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        UserRatingStore ratingStore,
        SuggestarrClient suggestarrClient,
        ILogger<RatingController> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _ratingStore = ratingStore;
        _suggestarrClient = suggestarrClient;
        _logger = logger;
    }

    /// <summary>Gets the current user's rating for an item.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>JSON object with stars (0-5).</returns>
    [HttpGet]
    public ActionResult<object> GetRating([FromQuery] Guid itemId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty || itemId == Guid.Empty)
        {
            return BadRequest("Missing userId or itemId.");
        }

        var stars = _ratingStore.GetRating(userId, itemId);
        return Ok(new { itemId, rating = stars });
    }

    /// <summary>Sets or updates a 1-5 star rating for an item.</summary>
    /// <param name="request">The rating request.</param>
    /// <returns>JSON object confirming the new rating.</returns>
    [HttpPost]
    public async Task<ActionResult<object>> SetRating([FromBody] SetRatingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty || request.ItemId == Guid.Empty)
        {
            return BadRequest("Missing userId or itemId.");
        }

        var stars = Math.Clamp(request.Rating, 0, 5);
        var item = _libraryManager.GetItemById(request.ItemId);
        var title = item?.Name;

        // 1. Persist to internal 1-5 star store
        _ratingStore.SetRating(userId, request.ItemId, stars, title);

        // 2. Synchronize to native Jellyfin UserData (scaled to 10-point scale)
        if (item is not null)
        {
            try
            {
                var user = _userManager.GetUserById(userId);
                if (user is not null)
                {
                    var userData = _userDataManager.GetUserData(user, item);
                    if (userData is not null)
                    {
                        if (stars > 0)
                        {
                            userData.Rating = stars * 2.0; // 5 stars = 10.0, 4 stars = 8.0
                            if (stars >= 4)
                            {
                                userData.Likes = true;
                            }
                            else if (stars <= 2)
                            {
                                userData.Likes = false;
                            }
                        }
                        else
                        {
                            userData.Rating = null;
                        }

                        _userDataManager.SaveUserData(
                            user,
                            item,
                            userData,
                            UserDataSaveReason.UpdateUserData,
                            CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AiSearch: failed to update native UserData for {ItemId}", request.ItemId);
            }

            // 3. Trigger Suggestarr auto-recommendation/download if rated highly
            var config = Plugin.Instance?.Configuration;
            if (config is not null && config.SuggestarrEnabled && config.SuggestarrAutoRequest && stars >= config.SuggestarrMinRating)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _suggestarrClient.OnItemRatedHighlyAsync(userId, item, stars, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AiSearch: Suggestarr background push failed for {Item}", item.Name);
                    }
                });
            }
        }

        return Ok(new { success = true, itemId = request.ItemId, rating = stars });
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var parsed) ? parsed : Guid.Empty;
    }
}
