using System.Collections.Generic;

namespace Dice.Models;

public class DiceDataModel
{
    public List<DiceRollRecord> History { get; set; } = [];
}
