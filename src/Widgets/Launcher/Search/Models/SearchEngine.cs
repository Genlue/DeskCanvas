using System.Text.Json.Serialization;
using Search.Locales;
using Search.Services;

namespace Search.Models;

public class SearchEngine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public string IconKey { get; set; } = "Google";
    public string IconColor { get; set; } = "#4285F4";
    public string Category { get; set; } = Locale.Search_Category_General;
    public bool IsEnabled { get; set; } = true;
    public bool IsCustom { get; set; } = false;
    public int Priority { get; set; } = 0;

    [JsonIgnore]
    public string SvgPath => SearchIconProvider.GetPath(IconKey);

    public SearchEngine Clone()
    {
        return new SearchEngine
        {
            Id = Id,
            Name = Name,
            UrlTemplate = UrlTemplate,
            IconKey = IconKey,
            IconColor = IconColor,
            Category = Category,
            IsEnabled = IsEnabled,
            IsCustom = IsCustom,
            Priority = Priority
        };
    }
}
