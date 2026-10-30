using System.Collections.Generic;

namespace Storage.Models;

public enum StorageDisplayMode
{
    ProgressBarList = 0,
    RingDashboard = 1
}

public enum StorageTextFormat
{
    FreeSpace = 0,
    UsedAndTotal = 1,
    Percentage = 2
}

public record StorageModel(
    StorageDisplayMode DisplayMode = StorageDisplayMode.ProgressBarList,
    StorageTextFormat TextFormat = StorageTextFormat.FreeSpace,
    bool ShowHeader = true,
    bool ShowRemovableDrives = true,
    bool ShowNetworkDrives = false,
    bool UseWarningColors = true,
    bool DoubleClickToOpen = true,
    List<string>? HiddenDrives = null
);
