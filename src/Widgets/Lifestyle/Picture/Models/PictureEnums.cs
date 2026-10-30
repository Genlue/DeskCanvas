namespace Picture.Models;

public enum PictureFitMode
{
    CustomCrop = 0, // Manual crop with CropX, CropY, Zoom
    Fill = 1,       // UniformToFill (center fill)
    Fit = 2         // Uniform (entire image visible)
}

public enum SlideshowInterval
{
    Seconds5 = 0,
    Seconds10 = 1,
    Seconds30 = 2,
    Minutes1 = 3,
    Minutes5 = 4,
    Minutes15 = 5,
    Minutes30 = 6,
    Hours1 = 7,
    Days1 = 8,
    Manual = 9
}

public enum PlayOrder
{
    Sequential = 0,
    Shuffle = 1,
    Fixed = 2
}
