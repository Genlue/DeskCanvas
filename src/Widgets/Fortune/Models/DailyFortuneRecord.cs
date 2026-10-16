using System;

namespace Fortune.Models;

public class DailyFortuneRecord
{
    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public DateTime DrawnTime { get; set; } = DateTime.Now;
    public FortuneItem Fortune { get; set; } = new();
    public bool IsFreeDraw { get; set; }
}
