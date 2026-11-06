using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.History;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Ratings;

/// <summary>
/// Subscribes to Jellyfin's native <see cref="IUserDataManager.UserDataSaved"/>
/// event to automatically capture ratings coming from native apps like Plezy,
/// Findroid, Swiftfin, Kodi, or Jellyfin Web. Converts native 0-10 or 1-5 ratings
/// into 1-5 star entries in <see cref="UserRatingStore"/>.
/// </summary>
public sealed class RatingSyncService : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly UserRatingStore _ratingStore;
    private readonly ILogger<RatingSyncService> _logger;

    /// <summary>Initializes a new instance of the <see cref="RatingSyncService"/> class.</summary>
    public RatingSyncService(
        IUserDataManager userDataManager,
        UserRatingStore ratingStore,
        ILogger<RatingSyncService> logger)
    {
        _userDataManager = userDataManager;
        _ratingStore = ratingStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e.Item is null || e.UserId == Guid.Empty)
            {
                return;
            }

            // If rating is present in UserData, convert to 1-5 stars.
            var rawRating = e.UserData?.Rating;
            if (rawRating.HasValue && rawRating.Value > 0)
            {
                var stars = NormalizeToStars(rawRating.Value);
                if (stars >= 1 && stars <= 5)
                {
                    var current = _ratingStore.GetRating(e.UserId, e.Item.Id);
                    if (current != stars)
                    {
                        _ratingStore.SetRating(e.UserId, e.Item.Id, stars, e.Item.Name);
                        _logger.LogInformation(
                            "AiSearch: synced external rating for {Item} ({User}): {Stars}★ (raw={Raw})",
                            e.Item.Name,
                            e.UserId,
                            stars,
                            rawRating.Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: error handling UserDataSaved event for item {ItemId}", e.Item?.Id);
        }
    }

    /// <summary>
    /// Normalizes a native Jellyfin rating (commonly 0.0-10.0 or 1.0-5.0) to 1-5 stars.
    /// </summary>
    public static int NormalizeToStars(double rating)
    {
        if (rating <= 0)
        {
            return 0;
        }

        // If client reported on 0-10 scale (common in Jellyfin/Plezy)
        if (rating > 5.0)
        {
            return Math.Clamp((int)Math.Round(rating / 2.0, MidpointRounding.AwayFromZero), 1, 5);
        }

        // If client reported on 1-5 scale directly
        return Math.Clamp((int)Math.Round(rating, MidpointRounding.AwayFromZero), 1, 5);
    }
}
