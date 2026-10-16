namespace Dice.Models;

public record DiceModel(
    int DiceCount = 2,
    DiceType Type = DiceType.D6,
    DiceFaceStyle FaceStyle = DiceFaceStyle.Dots,
    bool ShowSum = true
);
