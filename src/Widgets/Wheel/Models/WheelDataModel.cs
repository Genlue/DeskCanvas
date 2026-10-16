using System.Collections.Generic;

namespace Wheel.Models;

public class WheelDataModel
{
    public List<WheelTemplate> Templates { get; set; } = [];
    public List<WheelDecisionRecord> History { get; set; } = [];
}
