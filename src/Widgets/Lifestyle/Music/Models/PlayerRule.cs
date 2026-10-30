namespace Music.Models;

public class PlayerRule
{
    public string Name { get; set; } = string.Empty;
    public string MatchPattern { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public string? ExePath { get; set; }

    public PlayerRule() { }

    public PlayerRule(string name, string matchPattern, bool isEnabled = true, int priority = 0, string? exePath = null)
    {
        Name = name;
        MatchPattern = matchPattern;
        IsEnabled = isEnabled;
        Priority = priority;
        ExePath = exePath;
    }
}
