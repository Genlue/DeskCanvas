using System;

namespace Picture.Models;

public class PictureItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public double CropX { get; set; } = 0.5; // 0.0 (left) ~ 1.0 (right), 0.5 = center
    public double CropY { get; set; } = 0.5; // 0.0 (top) ~ 1.0 (bottom), 0.5 = center
    public double Zoom { get; set; } = 1.0;  // 1.0x ~ 3.0x
    public int RotateAngle { get; set; } = 0; // 0, 90, 180, 270

    public PictureItem Copy() => new()
    {
        Id = Id,
        Path = Path,
        Name = Name,
        CropX = CropX,
        CropY = CropY,
        Zoom = Zoom,
        RotateAngle = RotateAngle
    };
}
