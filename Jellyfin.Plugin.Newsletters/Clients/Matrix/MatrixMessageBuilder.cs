using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Newsletters.Configuration;
using Jellyfin.Plugin.Newsletters.Shared.Database;
using Jellyfin.Plugin.Newsletters.Shared.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Newsletters.Clients.Matrix;

/// <summary>
/// Builds HTML-formatted Matrix messages from newsletter data.
/// </summary>
public class MatrixMessageBuilder(
    Logger loggerInstance,
    SQLiteDatabase dbInstance,
    ILibraryManager libraryManager,
    IReadOnlyList<JsonFileObj> upcomingItems)
    : ClientBuilder(loggerInstance, dbInstance, libraryManager)
{
    /// <summary>
    /// Builds a test message for a single configuration.
    /// </summary>
    /// <param name="config">The Matrix configuration.</param>
    /// <returns>Tuple of (htmlBody, plainTextBody).</returns>
    public (string Html, string PlainText) BuildTestMessage(MatrixConfiguration config)
    {
        var html = new StringBuilder();
        var plain = new StringBuilder();

        html.Append("<h2>Jellyfin Newsletter — Test Message</h2>");
        html.Append("<hr>");
        html.Append("<h3>Test Movie (2024)</h3>");
        html.Append("<p>This is a test message from your Jellyfin Newsletter plugin.</p>");
        html.Append("<p><i>Sent from Jellyfin</i></p>");

        plain.AppendLine("Jellyfin Newsletter — Test Message");
        plain.AppendLine("---");
        plain.AppendLine("Test Movie (2024)");
        plain.AppendLine("This is a test message from your Jellyfin Newsletter plugin.");
        plain.AppendLine("---");
        plain.AppendLine("Sent from Jellyfin");

        return (html.ToString(), plain.ToString());
    }

    /// <summary>
    /// Builds messages from current newsletter data.
    /// </summary>
    /// <param name="systemId">The Jellyfin system ID for building URLs.</param>
    /// <param name="config">The Matrix configuration.</param>
    /// <returns>Collection of (htmlBody, plainTextBody, imagePath, uniqueImageName) tuples.</returns>
    public ReadOnlyCollection<(string Html, string PlainText, string? ImagePath, string UniqueImageName)>
        BuildMessagesFromNewsletterData(string systemId, MatrixConfiguration config)
    {
        var messages = new List<(string Html, string PlainText, string? ImagePath, string UniqueImageName)>();
        var libraryNameMap = BuildLibraryNameMap();

        try
        {
            Db.CreateConnection();

            var items = new Dictionary<string, JsonFileObj>();

            foreach (var row in Db.Query("SELECT * FROM CurrNewsletterData;"))
            {
                if (row is null)
                {
                    continue;
                }

                var item = JsonFileObj.ConvertToObj(row);
                if (!ShouldIncludeItem(item, config, "Matrix"))
                {
                    continue;
                }

                var key = $"{item.Title}_{item.EventType}";
                items.TryAdd(key, item);
            }

            // Add upcoming items
            if (upcomingItems != null && upcomingItems.Count > 0)
            {
                foreach (var item in upcomingItems)
                {
                    if (!ShouldIncludeItem(item, config, "Matrix"))
                    {
                        continue;
                    }

                    var key = $"{item.Title}_Upcoming";
                    items.TryAdd(key, item);
                }
            }

            // Sort: event type order → media type (movies first) → library name
            var eventTypeOrder = new Dictionary<string, int>
            {
                { "add", 0 }, { "update", 1 }, { "delete", 2 }, { "upcoming", 3 },
            };

            var sortedItems = items.Values
                .OrderBy(i => eventTypeOrder.GetValueOrDefault(i.EventType?.ToLowerInvariant() ?? "add", 0))
                .ThenBy(i => i.Type == "Movie" ? 0 : 1)
                .ThenBy(i => GetLibraryName(i.LibraryId, libraryNameMap))
                .ToList();

            foreach (var item in sortedItems)
            {
                string eventType = item.EventType?.ToLowerInvariant() ?? "add";
                string libraryName = eventType == "upcoming"
                    ? (item.LibraryId ?? string.Empty)
                    : GetLibraryName(item.LibraryId, libraryNameMap);

                var (html, plain) = BuildItemMessage(item, systemId, config, libraryName);
                var imagePath = config.ThumbnailEnabled ? item.PosterPath : null;
                var uniqueName = $"{item.Title}_{item.EventType}".Replace(" ", "_", StringComparison.Ordinal);
                messages.Add((html, plain, imagePath, uniqueName));
            }
        }
        catch (Exception e)
        {
            Logger.Error("Error building Matrix messages: " + e);
        }
        finally
        {
            Db.CloseConnection();
        }

        return messages.AsReadOnly();
    }

    private (string Html, string PlainText) BuildItemMessage(
        JsonFileObj item,
        string systemId,
        MatrixConfiguration config,
        string libraryName)
    {
        var html = new StringBuilder();
        var plain = new StringBuilder();

        var eventType = item.EventType?.ToLowerInvariant() ?? "add";
        var eventPrefix = GetEventDescriptionPrefixBase(eventType, libraryName);

        var title = item.Title ?? "Unknown Title";
        var year = item.PremiereYear ?? string.Empty;
        var titleWithYear = string.IsNullOrEmpty(year) ? title : $"{title} ({year})";

        // Build Jellyfin link
        var jellyfinUrl = string.Empty;
        if (!string.IsNullOrEmpty(Config.Hostname) && !string.IsNullOrEmpty(item.ItemID) && eventType != "upcoming")
        {
            jellyfinUrl = $"{Config.Hostname}/web/index.html#/details?id={item.ItemID}&serverId={systemId}&event={eventType}";
        }

        // HTML title
        if (!string.IsNullOrEmpty(jellyfinUrl))
        {
            html.Append(CultureInfo.InvariantCulture, $"<h3><a href=\"{jellyfinUrl}\">{titleWithYear}</a></h3>");
        }
        else
        {
            html.Append(CultureInfo.InvariantCulture, $"<h3>{titleWithYear}</h3>");
        }

        html.Append(CultureInfo.InvariantCulture, $"<p><b>{eventPrefix}</b></p>");

        // Plain text title
        plain.AppendLine(titleWithYear);
        plain.AppendLine(eventPrefix);

        // Description
        if (config.DescriptionEnabled && !string.IsNullOrEmpty(item.SeriesOverview))
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{item.SeriesOverview}</p>");
            plain.AppendLine(item.SeriesOverview);
        }

        // Episodes (for series)
        if (config.EpisodesEnabled && item.Type == "Series")
        {
            ReadOnlyCollection<NlDetailsJson> parsedInfoList = ParseSeriesInfo(item, upcomingItems);
            string seaEps = GetSeasonEpisodeBase(parsedInfoList);
            if (!string.IsNullOrWhiteSpace(seaEps))
            {
                html.Append("<p><b>Episodes:</b><br>");
                html.Append(seaEps.TrimEnd('\n').Replace("\n", "<br>", StringComparison.Ordinal));
                html.Append("</p>");
                plain.AppendLine("Episodes:");
                plain.Append(seaEps);
            }
        }

        // Upcoming release date
        if (eventType == "upcoming")
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Release Date: {item.PremiereYear ?? "N/A"}</p>");
            plain.AppendLine(CultureInfo.InvariantCulture, $"Release Date: {item.PremiereYear ?? "N/A"}");
        }

        // Rating
        if (config.RatingEnabled)
        {
            var ratingText = item.CommunityRating > 0
                ? item.CommunityRating.Value.ToString($"F{Config.CommunityRatingDecimalPlaces}", CultureInfo.InvariantCulture)
                : "N/A";
            html.Append(CultureInfo.InvariantCulture, $"<p>Rating: {ratingText}</p>");
            plain.AppendLine(CultureInfo.InvariantCulture, $"Rating: {ratingText}");
        }

        // PG Rating
        if (config.PGRatingEnabled)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Rated: {item.OfficialRating ?? "N/A"}</p>");
            plain.AppendLine(CultureInfo.InvariantCulture, $"Rated: {item.OfficialRating ?? "N/A"}");
        }

        // Duration
        if (config.DurationEnabled)
        {
            var durationText = item.RunTime > 0 ? $"{item.RunTime} min" : "N/A";
            html.Append(CultureInfo.InvariantCulture, $"<p>Duration: {durationText}</p>");
            plain.AppendLine(CultureInfo.InvariantCulture, $"Duration: {durationText}");
        }

        return (html.ToString(), plain.ToString());
    }
}
