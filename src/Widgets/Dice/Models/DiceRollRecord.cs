using System;
using System.Collections.Generic;

namespace Dice.Models;

public class DiceRollRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<int> Values { get; set; } = [];
    public int Sum { get; set; }
    public DiceType Type { get; set; } = DiceType.D6;
    public bool IsCriticalMax { get; set; }
}
