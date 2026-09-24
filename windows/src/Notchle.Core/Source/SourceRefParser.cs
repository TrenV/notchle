namespace Notchle.Core;

// STUB: agent W1 replaces this. Public API frozen.
public static class SourceRefParser
{
    /// Same accepted forms as the Swift SourceRef(string:): open.spotify.com links (locale
    /// segment, /embed/, query, fragment, missing scheme) and spotify:{kind}:{id} URIs.
    public static SourceRef? TryParse(string text) => null;
}
