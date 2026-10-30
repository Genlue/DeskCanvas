namespace ControlCenter.Models;

public class SingleControlModel
{
    public ControlItemType ItemType { get; set; } = ControlItemType.Volume;
    public int WheelStep { get; set; } = 2;
}
