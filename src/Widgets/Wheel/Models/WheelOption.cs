using System;

namespace Wheel.Models;

public class WheelOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
    public string ColorHex { get; set; } = "#0078D4";
    public bool IsEnabled { get; set; } = true;
}
