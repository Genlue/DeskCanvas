using System.Collections.Generic;

namespace Batteries.Models;

public enum DeviceKind
{
    Computer,
    Mouse,
    Keyboard,
    Headphones,
    Phone,
    Gamepad,
    Other
}

public record BatteriesModel(
    bool ShowPercentage = true,
    bool ShowPeripherals = true,
    bool ShowRemainingTime = true,
    Dictionary<string, DeviceKind>? DeviceTypeOverrides = null
);
