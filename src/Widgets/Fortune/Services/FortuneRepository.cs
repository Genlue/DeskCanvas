using System;
using System.Collections.Generic;
using Fortune.Models;

namespace Fortune.Services;

public static class FortuneRepository
{
    private static readonly Random Rnd = new();

    private static readonly List<FortuneItem> TraditionalList =
    [
        new FortuneItem
        {
            Id = 1,
            NumberName = "第一签 · 大吉",
            Grade = FortuneGrade.GreatLuck,
            GradeText = "大吉",
            PoemTitle = "日出扶桑",
            Poem = "巍巍独步向云间，玉殿千官第一班。\n富贵荣华天付与，福如东海寿如山。",
            Explanation = "运势极盛，天时地利俱全。所虑之事皆可迎刃而解，直管向前。",
            Suitable = "开启新篇、勇敢表达、深度思考",
            Taboo = "傲慢自负、粗心大意",
            LuckyColor = "胭脂红",
            LuckyColorHex = "#C83C23",
            LuckyNumber = 9,
            LuckyThing = "随身笔记本",
            Score = 99
        },
        new FortuneItem
        {
            Id = 2,
            NumberName = "第八签 · 上上签",
            Grade = FortuneGrade.UpperLuck,
            GradeText = "上上",
            PoemTitle = "风帆顺水",
            Poem = "风帆顺水正相逢，万里行舟不用工。\n长天日色清如画，一任优游向碧空。",
            Explanation = "谋事顺风顺水，阻力消弭于无形，顺势而为必大有所获。",
            Suitable = "乘胜追击、结交良友、户外散心",
            Taboo = "瞻前顾后、犹豫不决",
            LuckyColor = "天青色",
            LuckyColorHex = "#5698C3",
            LuckyNumber = 8,
            LuckyThing = "桌面绿植",
            Score = 96
        },
        new FortuneItem
        {
            Id = 3,
            NumberName = "第十六签 · 中吉",
            Grade = FortuneGrade.MediumLuck,
            GradeText = "中吉",
            PoemTitle = "幽兰逢春",
            Poem = "兰生深谷自芬芳，不逐繁华媚艳阳。\n忽遇东风吹秀气，清香引得远人忙。",
            Explanation = "前期沉潜厚积，今日渐露锋芒。无需急躁，自有懂你之人。",
            Suitable = "沉淀复盘、品茗听歌、专注代码",
            Taboo = "心浮气躁、急功近利",
            LuckyColor = "暮山紫",
            LuckyColorHex = "#7B68EE",
            LuckyNumber = 6,
            LuckyThing = "机械键盘",
            Score = 90
        },
        new FortuneItem
        {
            Id = 4,
            NumberName = "第二十八签 · 吉",
            Grade = FortuneGrade.GoodLuck,
            GradeText = "吉",
            PoemTitle = "竹石长青",
            Poem = "咬定青山不放松，立根原在破岩中。\n千磨万击还坚劲，任尔东西南北风。",
            Explanation = "定力便是最好的运气。坚守初心，外界杂音自会随风而逝。",
            Suitable = "规律作息、坚持运动、解决难关",
            Taboo = "盲从随大流、自我怀疑",
            LuckyColor = "竹青绿",
            LuckyColorHex = "#2E8B57",
            LuckyNumber = 7,
            LuckyThing = "降噪耳机",
            Score = 88
        },
        new FortuneItem
        {
            Id = 5,
            NumberName = "第三十六签 · 平安吉",
            Grade = FortuneGrade.PeaceLuck,
            GradeText = "平安",
            PoemTitle = "闲云野鹤",
            Poem = "莫向江头问故人，一溪流水两相亲。\n云收雨过天青处，正好登舟自在身。",
            Explanation = "无惊无险，平静即是上上签。今日适合休养生息，养精蓄锐。",
            Suitable = "充足睡眠、整理桌面、清淡饮食",
            Taboo = "高压连轴转、报复性熬夜",
            LuckyColor = "月光白",
            LuckyColorHex = "#F0F8FF",
            LuckyNumber = 2,
            LuckyThing = "陶瓷水杯",
            Score = 85
        },
        new FortuneItem
        {
            Id = 6,
            NumberName = "第四十九签 · 破厄吉",
            Grade = FortuneGrade.BreakCurse,
            GradeText = "破厄",
            PoemTitle = "枯木逢春",
            Poem = "雷轰电闪过重山，转眼晴岚入目前。\n休怨从前多坎坷，今朝春色满庭除。",
            Explanation = "前日积聚之晦气已被清晨破晓彻底荡涤，反转时刻来临！",
            Suitable = "断舍离、犒赏自己、向新出发",
            Taboo = "沉湎过去、内耗自责",
            LuckyColor = "琥珀金",
            LuckyColorHex = "#DAA520",
            LuckyNumber = 1,
            LuckyThing = "一杯热拿铁",
            Score = 92
        }
    ];

    private static readonly List<FortuneItem> CyberWorkerList =
    [
        new FortuneItem
        {
            Id = 101,
            NumberName = "赛博神谕 · 大吉",
            Grade = FortuneGrade.GreatLuck,
            GradeText = "大吉",
            PoemTitle = "编译一次过",
            Poem = "代码行行无警告，需求个个皆清晰。\n甲方点头称满意，准点打卡乐无边。",
            Explanation = "今日逻辑思维清晰敏捷，不仅产出高效，且有被夸奖的好彩头。",
            Suitable = "Commit提交、喝杯奶茶、大胆提方案",
            Taboo = "给自己的代码下断言'肯定没Bug'",
            LuckyColor = "赛博蓝",
            LuckyColorHex = "#0078D4",
            LuckyNumber = 0,
            LuckyThing = "工牌挂绳",
            Score = 98
        },
        new FortuneItem
        {
            Id = 102,
            NumberName = "赛博神谕 · 吉",
            Grade = FortuneGrade.GoodLuck,
            GradeText = "吉",
            PoemTitle = "优雅摸鱼",
            Poem = "键盘敲得噼啪响，实际是在看文章。\n工作按部就班走，身心舒畅自安康。",
            Explanation = "劳逸结合之道在于心平气和。节奏掌握在自己手里，效率更高。",
            Suitable = "起身伸懒腰、眺望窗外、多喝温水",
            Taboo = "长时间低头、卷死同事",
            LuckyColor = "薄荷绿",
            LuckyColorHex = "#3CB371",
            LuckyNumber = 5,
            LuckyThing = "眼药水",
            Score = 89
        },
        new FortuneItem
        {
            Id = 103,
            NumberName = "赛博神谕 · 破厄吉",
            Grade = FortuneGrade.BreakCurse,
            GradeText = "破厄",
            PoemTitle = "Bug自动愈合",
            Poem = "昨夜谜团百思惑，今朝重启竟如初。\n不是神迹显灵验，只缘执念已消除。",
            Explanation = "纠缠多日的难题今天会柳暗花明，有时候放轻松反而一通百通。",
            Suitable = "重启电脑、重构老代码、洗把脸",
            Taboo = "和代码死磕到底不吃饭",
            LuckyColor = "霓虹橙",
            LuckyColorHex = "#FF8C00",
            LuckyNumber = 3,
            LuckyThing = "护腕鼠标垫",
            Score = 93
        }
    ];

    public static FortuneItem GetRandomFortune(FortunePoolType poolType)
    {
        var list = poolType switch
        {
            FortunePoolType.CyberWorker => CyberWorkerList,
            _ => TraditionalList
        };

        return list[Rnd.Next(list.Count)];
    }
}
