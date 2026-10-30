using System;
using System.Diagnostics;
using Search.Models;

namespace Search.Services;

public static class SearchLauncher
{
    public static bool Launch(SearchEngine engine, string query)
    {
        try
        {
            var url = BuildUrl(engine, query);
            if (string.IsNullOrWhiteSpace(url)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildUrl(SearchEngine engine, string query)
    {
        if (engine == null || string.IsNullOrWhiteSpace(engine.UrlTemplate))
            return "https://www.google.com";

        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            // If query is empty, navigate to the base host/domain
            if (Uri.TryCreate(engine.UrlTemplate, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Authority}";
            }
            return engine.UrlTemplate.Replace("{q}", "").Replace("?q=", "").Replace("&q=", "");
        }

        var encoded = Uri.EscapeDataString(trimmed);
        if (engine.UrlTemplate.Contains("{q}"))
        {
            return engine.UrlTemplate.Replace("{q}", encoded);
        }

        // Append if {q} placeholder not present
        var separator = engine.UrlTemplate.Contains('?') ? "&q=" : "?q=";
        return $"{engine.UrlTemplate}{separator}{encoded}";
    }
}
