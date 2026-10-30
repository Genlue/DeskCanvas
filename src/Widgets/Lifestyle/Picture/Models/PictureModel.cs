using System.Collections.Generic;

namespace Picture.Models;

public record PictureModel(
    List<PictureItem>? Items = null,
    int CurrentIndex = 0,
    SlideshowInterval Interval = SlideshowInterval.Minutes5,
    PlayOrder Order = PlayOrder.Sequential,
    PictureFitMode FitMode = PictureFitMode.CustomCrop,
    bool IsFrameless = false,
    bool ShowCaption = false,
    int CornerRadius = 0,
    bool ClickToNext = true,
    bool DoubleClickToOpen = true
)
{
    public List<PictureItem> GetItems() => Items ?? [];
}
