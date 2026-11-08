// Copyright (C) 2024-2026 Franciskid, FaserF
// Licensed under GNU General Public License v3.0 (GPL-3.0)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AiSearch.Tests;

public class AiSearchTests
{
    [Fact]
    public void TitleMatcher_DirectMatches_WorkAccurately()
    {
        var movie = new Movie
        {
            Name = "Inception",
            OriginalTitle = "Inception"
        };

        TitleMatcher.IsTitleMatch(movie, "Inception").Should().BeTrue();
        TitleMatcher.IsTitleMatch(movie, "incept").Should().BeTrue();
        TitleMatcher.IsTitleMatch(movie, "Matrix").Should().BeFalse();
    }

    [Theory]
    [InlineData("Inception", false)]
    [InlineData("The Dark Knight", false)]
    [InlineData("Breaking Bad", false)]
    [InlineData("spannungsgeladener thriller im weltall", true)]
    [InlineData("movies about space exploration", true)]
    [InlineData("can you recommend something good?", true)]
    public void TitleMatcher_LooksLikeSentenceQuery_DistinguishesTitlesFromSentences(string query, bool expectedSentence)
    {
        TitleMatcher.LooksLikeSentenceQuery(query).Should().Be(expectedSentence);
    }

    [Fact]
    public void PluginConfiguration_ResolveLocale_HandlesGermanEnglishFrench()
    {
        var cfg = new PluginConfiguration { UiLanguage = "auto" };
        cfg.ResolveLocale("de-DE").Should().Be("de");
        cfg.ResolveLocale("de").Should().Be("de");
        cfg.ResolveLocale("fr-FR").Should().Be("fr");
        cfg.ResolveLocale("en-US").Should().Be("en");
        cfg.ResolveLocale(null).Should().Be("en");

        cfg.UiLanguage = "de";
        cfg.ResolveLocale("en-US").Should().Be("de");

        cfg.UiLanguage = "en";
        cfg.ResolveLocale("de-DE").Should().Be("en");
    }

    [Fact]
    public void PluginConfiguration_GetEffectiveChatEndpointUrl_ReturnsCorrectDefaults()
    {
        var cfg = new PluginConfiguration { ChatProvider = "omniroute", ChatEndpointUrl = "" };
        cfg.GetEffectiveChatEndpointUrl().Should().Be("http://omniroute:20128/v1/chat/completions");

        cfg.ChatProvider = "openai";
        cfg.GetEffectiveChatEndpointUrl().Should().Be("https://api.openai.com/v1/chat/completions");

        cfg.ChatProvider = "anthropic";
        cfg.GetEffectiveChatEndpointUrl().Should().Be("https://api.anthropic.com/v1/messages");

        cfg.ChatProvider = "gemini";
        cfg.GetEffectiveChatEndpointUrl().Should().Be("https://generativelanguage.googleapis.com/v1beta/models");

        cfg.ChatEndpointUrl = "https://my-custom-proxy.internal/v1/chat/completions";
        cfg.GetEffectiveChatEndpointUrl().Should().Be("https://my-custom-proxy.internal/v1/chat/completions");
    }

    [Fact]
    public void PluginConfiguration_GetEffectiveModelsAndEmbeddingUrls_WorkForOmniRoute()
    {
        var cfg = new PluginConfiguration { ChatProvider = "omniroute" };
        cfg.GetEffectiveModelsEndpointUrl().Should().Be("http://omniroute:20128/v1/models");
        cfg.GetEffectiveEmbeddingEndpointUrl().Should().Be("http://omniroute:20128/v1/embeddings");
        cfg.GetEffectiveModel().Should().Be("auto/fast");

        var target = EmbeddingsTarget.FromConfiguration(cfg);
        target.Should().NotBeNull();
        target!.BaseUrl.Should().Be("http://omniroute:20128/v1/embeddings");
        target.Model.Should().Be("local-onnx/local-onnx-int8");
    }

    [Fact]
    public void PromptBuilder_BuildMessages_SupportsGerman()
    {
        var candidates = new List<CandidateMovie>
        {
            new(new Movie { Name = "Inception", ProductionYear = 2010 }, "Leonardo DiCaprio, Joseph Gordon-Levitt")
        };

        var messages = PromptBuilder.BuildMessages(
            "spannende Sci-Fi",
            "de",
            5,
            candidates,
            new List<string>(),
            new List<string>(),
            true,
            200);

        messages.Should().HaveCount(2);
        var json = JsonSerializer.Serialize(messages);
        json.Should().Contain("German (Deutsch)");
    }

    [Fact]
    public void PromptBuilder_BuildInterviewMessages_SupportsGerman()
    {
        var messages = PromptBuilder.BuildInterviewMessages("Ich will einen Film schauen", "de");
        var json = JsonSerializer.Serialize(messages);
        json.Should().Contain("German (Deutsch)");
    }

    [Fact]
    public void PromptBuilder_BuildExplainMessages_SupportsGerman()
    {
        var items = new List<BaseItem>
        {
            new Movie { Name = "Interstellar", ProductionYear = 2014 }
        };

        var messages = PromptBuilder.BuildExplainMessages("Weltraum Zeitreise", "de", items);
        var json = JsonSerializer.Serialize(messages);
        json.Should().Contain("German (Deutsch)");
    }

    [Fact]
    public void SemanticSearchProvider_AllowedKinds_FiltersCorrectly()
    {
        var query = new SearchProviderQuery
        {
            SearchTerm = "test",
            IncludeItemTypes = new[] { BaseItemKind.Movie }
        };

        var allowed = SemanticSearchProvider.AllowedKinds(query);
        allowed.Should().ContainSingle().Which.Should().Be(BaseItemKind.Movie);
    }

    [Fact]
    public void SemanticSearchProvider_AllowedKinds_PreservesSeriesWhenVideoRequested()
    {
        var query = new SearchProviderQuery
        {
            SearchTerm = "test",
            MediaTypes = new[] { MediaType.Video }
        };

        var allowed = SemanticSearchProvider.AllowedKinds(query);
        allowed.Should().Contain(BaseItemKind.Movie);
        allowed.Should().Contain(BaseItemKind.Series);
        allowed.Should().Contain(BaseItemKind.Episode);
    }

    [Theory]
    [InlineData(10.0, 5)]
    [InlineData(9.0, 5)]
    [InlineData(8.0, 4)]
    [InlineData(7.0, 4)]
    [InlineData(6.0, 3)]
    [InlineData(5.0, 5)] // 1-5 scale directly
    [InlineData(4.0, 4)]
    [InlineData(3.0, 3)]
    [InlineData(2.0, 2)]
    [InlineData(1.0, 1)]
    [InlineData(0.0, 0)]
    public void RatingSyncService_NormalizeToStars_ConvertsCorrectly(double rawRating, int expectedStars)
    {
        Jellyfin.Plugin.AiSearch.Ratings.RatingSyncService.NormalizeToStars(rawRating).Should().Be(expectedStars);
    }

    [Fact]
    public void PromptBuilder_BuildMessages_IncludesUserRatingsDirectives()
    {
        var candidates = new List<CandidateMovie>
        {
            new(new Movie { Name = "Inception", ProductionYear = 2010 }, "Leonardo DiCaprio")
        };

        var loved = new List<string> { "Interstellar (2014)", "The Dark Knight (2008)" };
        var disliked = new List<string> { "Cats (2019)" };

        var messages = PromptBuilder.BuildMessages(
            "Sci-Fi",
            "en",
            5,
            candidates,
            new List<string>(),
            new List<string>(),
            true,
            200,
            tasteProfile: "Loves mind-bending thrillers.",
            lovedRatings: loved,
            dislikedRatings: disliked);

        var json = JsonSerializer.Serialize(messages);
        json.Should().Contain("USER_LOVED (4-5 stars): Interstellar (2014); The Dark Knight (2008)");
        json.Should().Contain("USER_DISLIKED (1-2 stars): Cats (2019)");
        json.Should().Contain("USER_TASTE_PROFILE: Loves mind-bending thrillers.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void SemanticSearchProvider_CanSearch_ReturnsFalseForEmptyOrTooShort(string? queryText)
    {
        var appPathsMock = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPathsMock.Setup(x => x.PluginsPath).Returns(Path.GetTempPath());
        appPathsMock.Setup(x => x.PluginConfigurationsPath).Returns(Path.GetTempPath());
        var xmlSerializerMock = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();

        _ = new Plugin(appPathsMock.Object, xmlSerializerMock.Object);

        var provider = new SemanticSearchProvider(
            null!, null!, null!, null!,
            new Mock<Microsoft.AspNetCore.Http.IHttpContextAccessor>().Object,
            null!,
            new Mock<Microsoft.Extensions.Logging.ILogger<SemanticSearchProvider>>().Object);

        var query = new SearchProviderQuery { SearchTerm = queryText! };
        provider.CanSearch(query).Should().BeFalse();
    }
}
