// Copyright (C) 2024-2026 Franciskid, FaserF
// Licensed under GNU General Public License v3.0 (GPL-3.0)

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;
using Jellyfin.Plugin.AiSearch.Recommend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>Request payload for testing an AI chat connection.</summary>
public sealed class TestChatRequest
{
    /// <summary>Gets or sets the provider name.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the endpoint URL.</summary>
    public string? EndpointUrl { get; set; }

    /// <summary>Gets or sets the API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the model id.</summary>
    public string? Model { get; set; }
}

/// <summary>Controller for testing chat model endpoints.</summary>
[ApiController]
[Route("AiSearch")]
[Authorize(AuthenticationSchemes = "CustomAuthentication")]
public sealed class AiTestController : ControllerBase
{
    private readonly DirectChatClient _chat;

    /// <summary>Initializes a new instance of the <see cref="AiTestController"/> class.</summary>
    /// <param name="chat">Outbound chat client.</param>
    public AiTestController(DirectChatClient chat)
    {
        _chat = chat;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Tests connection to a specified or configured chat endpoint.</summary>
    [HttpPost("TestChat")]
    public async Task<ActionResult> TestChat([FromBody] TestChatRequest request, CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var config = Config;
        var tempConfig = new PluginConfiguration
        {
            ChatProvider = !string.IsNullOrWhiteSpace(request.Provider) ? request.Provider : config.ChatProvider,
            ChatEndpointUrl = !string.IsNullOrWhiteSpace(request.EndpointUrl) ? request.EndpointUrl : config.ChatEndpointUrl,
            ChatApiKey = request.ApiKey ?? config.ChatApiKey,
            TimeoutSeconds = 15
        };

        var targetUrl = tempConfig.GetEffectiveChatEndpointUrl();
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim() : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4o-mini";
        }

        var messages = new object[]
        {
            new { role = "user", content = "Reply with exactly: {\"answer\":\"OK\",\"recommendations\":[]}" }
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var content = await _chat.CompleteAsync(tempConfig, targetUrl, tempConfig.ChatApiKey, model, messages, cts.Token).ConfigureAwait(false);
            sw.Stop();

            return Ok(new
            {
                success = true,
                latencyMs = sw.ElapsedMilliseconds,
                message = $"Connected successfully in {sw.ElapsedMilliseconds}ms (Model: {model})",
                responseSnippet = content.Length > 80 ? content[..80] + "…" : content
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(new
            {
                success = false,
                latencyMs = sw.ElapsedMilliseconds,
                error = ex.Message
            });
        }
    }
}
