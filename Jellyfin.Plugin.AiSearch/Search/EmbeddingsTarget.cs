using System;
using Jellyfin.Plugin.AiSearch.Configuration;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Where and how to compute embeddings: an OpenAI-compatible base URL, an
/// optional bearer key (local Ollama needs none), and the embedding model id.
/// </summary>
public sealed record EmbeddingsTarget(string BaseUrl, string ApiKey, string Model, int TimeoutSeconds)
{
    /// <summary>
    /// Resolves the embeddings target from the plugin configuration. The local
    /// index needs a dedicated embeddings endpoint and model; when either is
    /// missing the index is disabled and the plugin falls back to a catalog slice.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>The target, or <c>null</c> when the local index is not configured.</returns>
    public static EmbeddingsTarget? FromConfiguration(PluginConfiguration config)
    {
        var effectiveUrl = config.GetEffectiveEmbeddingEndpointUrl();
        var effectiveModel = !string.IsNullOrWhiteSpace(config.EmbeddingModel)
            ? config.EmbeddingModel.Trim()
            : (string.Equals(config.ChatProvider, "omniroute", StringComparison.OrdinalIgnoreCase) ? "local-onnx/local-onnx-int8" : string.Empty);

        if (string.IsNullOrWhiteSpace(effectiveModel) || string.IsNullOrWhiteSpace(effectiveUrl))
        {
            return null;
        }

        var key = !string.IsNullOrWhiteSpace(config.EmbeddingApiKey)
            ? config.EmbeddingApiKey.Trim()
            : config.ChatApiKey?.Trim() ?? string.Empty;

        return new EmbeddingsTarget(
            effectiveUrl.TrimEnd('/'),
            key,
            effectiveModel,
            Math.Clamp(config.TimeoutSeconds, 5, 120));
    }
}
