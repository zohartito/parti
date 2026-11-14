// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Newtonsoft.Json.Linq;

namespace Parti.Providers.Ollama;

/// <summary>
/// Configuration for <see cref="OllamaProvider"/>. The provider snapshots these values when it is
/// constructed, so later changes to an options instance never redirect an existing provider.
/// </summary>
public sealed class OllamaProviderOptions
{
    /// <summary>
    /// Default local server.
    /// </summary>
    public static readonly Uri DefaultBaseUrl = new("http://localhost:11434");

    /// <summary>
    /// Server root (without <c>/api</c>). Validated: http/https, no user-info, query or fragment.
    /// </summary>
    public Uri BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Context window sent as <c>options.num_ctx</c> on every chat call. Always sent, because
    /// Ollama's small default context silently truncates the system prompt and tool schemas.
    /// </summary>
    public int NumCtx { get; set; } = 32_768;

    /// <summary>
    /// <c>keep_alive</c> sent on chat and warm-up calls. Null or blank omits the field.
    /// </summary>
    public string? KeepAlive { get; set; } = "10m";

    /// <summary>
    /// Optional <c>think</c> value: a boolean, or a level string ("low", "medium", "high") for
    /// models such as gpt-oss. Null (the default) omits the field and uses the model default.
    /// </summary>
    public JToken? Think { get; set; }

    /// <summary>
    /// Returns a detached copy (the <see cref="Think"/> token is deep-cloned).
    /// </summary>
    public OllamaProviderOptions Clone()
    {
        return new OllamaProviderOptions
        {
            BaseUrl = BaseUrl,
            NumCtx = NumCtx,
            KeepAlive = KeepAlive,
            Think = Think?.DeepClone(),
        };
    }
}
