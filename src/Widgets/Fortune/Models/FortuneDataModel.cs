using System.Collections.Generic;

namespace Fortune.Models;

public class FortuneDataModel
{
    public DailyFortuneRecord? TodayRecord { get; set; }
    public List<DailyFortuneRecord> History { get; set; } = [];
}
