namespace Fortune.Models;

public record FortuneModel(
    FortunePoolType PoolType = FortunePoolType.Traditional,
    bool ShowLuckyDetails = true,
    bool FreeDrawMode = false
);
