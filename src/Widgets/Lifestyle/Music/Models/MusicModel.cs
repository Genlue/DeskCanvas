using Music.Services;

namespace Music.Models;

public class MusicModel
{
    public List<PlayerRule> PlayerRules { get; set; } = [];
    public bool AmbientGlow { get; set; } = true;
    public bool ShowProgressBar { get; set; } = true;
    public bool AutoDetect { get; set; } = true;

    public MusicModel()
    {
        PlayerRules = PresetPlayers.CreateDefaultRules();
    }

    public MusicModel(List<PlayerRule>? rules, bool ambientGlow = true, bool showProgressBar = true, bool autoDetect = true)
    {
        PlayerRules = rules is { Count: > 0 } ? rules : PresetPlayers.CreateDefaultRules();
        AmbientGlow = ambientGlow;
        ShowProgressBar = showProgressBar;
        AutoDetect = autoDetect;
    }
}
