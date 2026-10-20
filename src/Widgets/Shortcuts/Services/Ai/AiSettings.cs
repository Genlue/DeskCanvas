namespace Shortcuts.Services.Ai;

/// <summary>
/// Connection settings for an OpenAI-compatible chat-completions endpoint used by the
/// AI workflow assistant. <see cref="BaseUrl"/> may point at any OpenAI-compatible host
/// (filled up to <c>/v1</c>); the <c>/chat/completions</c> suffix is appended by the caller.
/// </summary>
public sealed class AiSettings
{
    /// <summary>Endpoint root, e.g. "https://api.openai.com/v1".</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Bearer token sent as the Authorization header.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Model id understood by the endpoint, e.g. "gpt-4o-mini".</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Sampling temperature (low by default: workflow JSON wants determinism).</summary>
    public double Temperature { get; set; } = 0.2;
}
