using System.Collections.Generic;

namespace Search.Models;

public record SearchModel
{
    public string CurrentEngineId { get; init; } = "google";
    public List<SearchEngine> CustomEngines { get; init; } = [];
    public bool ClearInputAfterSearch { get; init; } = true;
    public bool SaveHistory { get; init; } = true;
    public List<string> RecentHistory { get; init; } = [];
}
