// 目录是只读数据：顺序即弹窗里 chip 的展示顺序，id 一旦发布就不要改，
// 否则用户已经存下来的引用（以及测试里的断言）会失去意义。
using System;
using System.Collections.Generic;
using System.Linq;

namespace Countdown.FestivalPreset;

public static class FestivalPresetCatalog
{
    public static IReadOnlyList<FestivalPreset> All { get; } =
    [
        // 公历固定日期
        new("festival.new-year", "Countdown_Festival_NewYear", FestivalDateKind.SolarFixed,
            "Holiday", "#FF5A5F", Month: 1, Day: 1),
        new("festival.valentines-day", "Countdown_Festival_ValentinesDay", FestivalDateKind.SolarFixed,
            "Anniversary", "#FF5A5F", Month: 2, Day: 14),
        new("festival.labor-day", "Countdown_Festival_LaborDay", FestivalDateKind.SolarFixed,
            "Holiday", "#0A84FF", Month: 5, Day: 1),
        new("festival.childrens-day", "Countdown_Festival_ChildrensDay", FestivalDateKind.SolarFixed,
            "Holiday", "#FF9F0A", Month: 6, Day: 1),
        new("festival.teachers-day", "Countdown_Festival_TeachersDay", FestivalDateKind.SolarFixed,
            "Holiday", "#30D158", Month: 9, Day: 10),
        new("festival.national-day", "Countdown_Festival_NationalDay", FestivalDateKind.SolarFixed,
            "Holiday", "#FF5A5F", Month: 10, Day: 1),
        new("festival.halloween", "Countdown_Festival_Halloween", FestivalDateKind.SolarFixed,
            "Holiday", "#FF9F0A", Month: 10, Day: 31),
        new("festival.singles-day", "Countdown_Festival_SinglesDay", FestivalDateKind.SolarFixed,
            "Life", "#BF5AF2", Month: 11, Day: 11),
        new("festival.christmas-eve", "Countdown_Festival_ChristmasEve", FestivalDateKind.SolarFixed,
            "Holiday", "#30D158", Month: 12, Day: 24),
        new("festival.christmas", "Countdown_Festival_Christmas", FestivalDateKind.SolarFixed,
            "Holiday", "#FF5A5F", Month: 12, Day: 25),

        // 农历固定日期
        new("festival.spring-festival", "Countdown_Festival_SpringFestival", FestivalDateKind.LunarFixed,
            "Holiday", "#FF5A5F", LunarMonth: 1, LunarDay: 1),
        new("festival.lantern", "Countdown_Festival_Lantern", FestivalDateKind.LunarFixed,
            "Holiday", "#FF9F0A", LunarMonth: 1, LunarDay: 15),
        new("festival.longtaitou", "Countdown_Festival_Longtaitou", FestivalDateKind.LunarFixed,
            "Holiday", "#30D158", LunarMonth: 2, LunarDay: 2),
        new("festival.dragon-boat", "Countdown_Festival_DragonBoat", FestivalDateKind.LunarFixed,
            "Holiday", "#30D158", LunarMonth: 5, LunarDay: 5),
        new("festival.qixi", "Countdown_Festival_Qixi", FestivalDateKind.LunarFixed,
            "Anniversary", "#BF5AF2", LunarMonth: 7, LunarDay: 7),
        new("festival.ghost", "Countdown_Festival_Ghost", FestivalDateKind.LunarFixed,
            "Holiday", "#FF9F0A", LunarMonth: 7, LunarDay: 15),
        new("festival.mid-autumn", "Countdown_Festival_MidAutumn", FestivalDateKind.LunarFixed,
            "Holiday", "#FF9F0A", LunarMonth: 8, LunarDay: 15),
        new("festival.double-ninth", "Countdown_Festival_DoubleNinth", FestivalDateKind.LunarFixed,
            "Holiday", "#FF9F0A", LunarMonth: 9, LunarDay: 9),
        new("festival.laba", "Countdown_Festival_Laba", FestivalDateKind.LunarFixed,
            "Holiday", "#FF9F0A", LunarMonth: 12, LunarDay: 8),
        new("festival.new-years-eve", "Countdown_Festival_NewYearsEve", FestivalDateKind.LunarNewYearsEve,
            "Holiday", "#FF5A5F", LunarMonth: 12, LunarDay: 30),

        // 浮动公历：某月的第 N 个星期 X
        new("festival.mothers-day", "Countdown_Festival_MothersDay", FestivalDateKind.SolarNthWeekday,
            "Holiday", "#FF5A5F", Month: 5, Ordinal: 2, Weekday: DayOfWeek.Sunday),
        new("festival.fathers-day", "Countdown_Festival_FathersDay", FestivalDateKind.SolarNthWeekday,
            "Holiday", "#0A84FF", Month: 6, Ordinal: 3, Weekday: DayOfWeek.Sunday),
        new("festival.thanksgiving", "Countdown_Festival_Thanksgiving", FestivalDateKind.SolarNthWeekday,
            "Holiday", "#FF9F0A", Month: 11, Ordinal: 4, Weekday: DayOfWeek.Thursday),

        // 节气：解析器里按近似规则取 4 月 4/5 日
        new("festival.qingming", "Countdown_Festival_Qingming", FestivalDateKind.SolarTermApprox,
            "Holiday", "#30D158", Month: 4, Day: 4)
    ];

    public static FestivalPreset? Find(string id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));
}
