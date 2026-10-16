namespace Habits.Models;

public enum HabitType
{
    Boolean,    // 完成打卡 (是/否)
    Numeric     // 定量目标打卡 (如喝水 2000ml，每次步进 250ml)
}

public enum HabitFrequency
{
    Daily,          // 每天
    Weekdays,       // 工作日 (Mon-Fri)
    Weekends        // 周末 (Sat-Sun)
}

public enum HabitsViewMode
{
    List,       // 多习惯列表
    Focus,      // 单习惯聚焦 (大图标 + 连续天数 + 本周圆点)
    Heatmap     // 热力图点阵矩阵
}
