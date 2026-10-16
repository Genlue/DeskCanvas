using System;

namespace Wheel.Models;

public class WheelDecisionRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TemplateTitle { get; set; } = string.Empty;
    public string SelectedOption { get; set; } = string.Empty;
}
