using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.History;

/// <summary>Represents a single user rating (1-5 stars) for a media item.</summary>
public sealed class UserRatingEntry
{
    /// <summary>Gets or sets the item id in N format.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the star rating (1 to 5).</summary>
    public int Stars { get; set; }

    /// <summary>Gets or sets the optional title for quick inspection.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets when the rating was set (UTC ISO-8601).</summary>
    public string RatedAt { get; set; } = string.Empty;
}

/// <summary>Container file model for user ratings.</summary>
public sealed class UserRatingCollection
{
    /// <summary>Gets or sets the list of ratings.</summary>
    public List<UserRatingEntry> Ratings { get; set; } = new();
}

/// <summary>
/// Thread-safe per-user rating store persisting 1-5 star ratings to disk at
/// <c>aisearch/ratings/{userId}.json</c>. Synchronized with native Jellyfin
/// <see cref="MediaBrowser.Controller.Library.IUserDataManager"/>.
/// </summary>
public sealed class UserRatingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly ILogger<UserRatingStore> _logger;
    private readonly ConcurrentDictionary<Guid, object> _locks = new();

    /// <summary>Initializes a new instance of the <see cref="UserRatingStore"/> class.</summary>
    /// <param name="paths">Application paths for the data folder.</param>
    /// <param name="logger">Logger.</param>
    public UserRatingStore(IApplicationPaths paths, ILogger<UserRatingStore> logger)
    {
        _dir = Path.Combine(paths.DataPath, "aisearch", "ratings");
        _logger = logger;
    }

    /// <summary>Gets the user's rating for a specific item, or 0 if unrated.</summary>
    public int GetRating(Guid userId, Guid itemId)
    {
        var ratings = Load(userId);
        var idStr = itemId.ToString("N");
        var match = ratings.FirstOrDefault(r => string.Equals(r.ItemId, idStr, StringComparison.OrdinalIgnoreCase));
        return match?.Stars ?? 0;
    }

    /// <summary>Sets or updates a rating (1-5) for an item. 0 removes the rating.</summary>
    public void SetRating(Guid userId, Guid itemId, int stars, string? title = null)
    {
        var fileLock = _locks.GetOrAdd(userId, _ => new object());
        lock (fileLock)
        {
            var list = LoadUnlocked(userId);
            var idStr = itemId.ToString("N");
            list.RemoveAll(r => string.Equals(r.ItemId, idStr, StringComparison.OrdinalIgnoreCase));

            if (stars is >= 1 and <= 5)
            {
                list.Add(new UserRatingEntry
                {
                    ItemId = idStr,
                    Stars = stars,
                    Title = title,
                    RatedAt = DateTime.UtcNow.ToString("o")
                });
            }

            SaveUnlocked(userId, list);
        }
    }

    /// <summary>Gets items rated high (default >= 4 stars), newest first.</summary>
    public IReadOnlyList<UserRatingEntry> GetTopRated(Guid userId, int minStars = 4, int limit = 25)
    {
        return Load(userId)
            .Where(r => r.Stars >= minStars)
            .OrderByDescending(r => r.RatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>Gets items rated low (default &lt;= 2 stars), newest first.</summary>
    public IReadOnlyList<UserRatingEntry> GetLowRated(Guid userId, int maxStars = 2, int limit = 25)
    {
        return Load(userId)
            .Where(r => r.Stars <= maxStars)
            .OrderByDescending(r => r.RatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>Loads all ratings for a user.</summary>
    public IReadOnlyList<UserRatingEntry> Load(Guid userId)
    {
        var fileLock = _locks.GetOrAdd(userId, _ => new object());
        lock (fileLock)
        {
            return LoadUnlocked(userId);
        }
    }

    private List<UserRatingEntry> LoadUnlocked(Guid userId)
    {
        var path = PathFor(userId);
        try
        {
            if (!File.Exists(path))
            {
                return new List<UserRatingEntry>();
            }

            var text = File.ReadAllText(path);
            var col = JsonSerializer.Deserialize<UserRatingCollection>(text, JsonOpts);
            return col?.Ratings ?? new List<UserRatingEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not read ratings for {User}", userId);
            return new List<UserRatingEntry>();
        }
    }

    private void SaveUnlocked(Guid userId, List<UserRatingEntry> ratings)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(userId);
            var temp = path + ".tmp";
            var col = new UserRatingCollection { Ratings = ratings };
            File.WriteAllText(temp, JsonSerializer.Serialize(col, JsonOpts));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not write ratings for {User}", userId);
        }
    }

    private string PathFor(Guid userId) => Path.Combine(_dir, userId.ToString("N") + ".json");
}
