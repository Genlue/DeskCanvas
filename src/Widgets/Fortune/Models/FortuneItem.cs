namespace Fortune.Models;

public class FortuneItem
{
    public int Id { get; set; }
    public string NumberName { get; set; } = string.Empty;
    public FortuneGrade Grade { get; set; } = FortuneGrade.GreatLuck;
    public string GradeText { get; set; } = "大吉";
    public string PoemTitle { get; set; } = string.Empty;
    public string Poem { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Suitable { get; set; } = string.Empty;
    public string Taboo { get; set; } = string.Empty;
    public string LuckyColor { get; set; } = "天青";
    public string LuckyColorHex { get; set; } = "#5698C3";
    public int LuckyNumber { get; set; } = 8;
    public string LuckyThing { get; set; } = "咖啡";
    public int Score { get; set; } = 95;
}
