using System;
using System.Collections.Generic;

namespace Wheel.Models;

public class WheelTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public List<WheelOption> Options { get; set; } = [];
    public bool EliminateOnSelected { get; set; }
}
