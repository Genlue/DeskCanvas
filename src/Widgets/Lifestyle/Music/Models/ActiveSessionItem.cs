namespace Music.Models;

public class ActiveSessionItem
{
    public string Title { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;

    public ActiveSessionItem() { }

    public ActiveSessionItem(string title, string appId)
    {
        Title = title;
        AppId = appId;
    }
}
