using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlCenter.Models;

public enum ControlItemType
{
    Brightness = 0,
    Volume = 1,
    AudioOutput = 2,
    Microphone = 3,
    WiFi = 4,
    Bluetooth = 5,
    DarkMode = 6,
    DesktopIcons = 7
}

public class ControlTilePlacement
{
    public ControlItemType Type { get; set; }
    public int Column { get; set; }
    public int Row { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    public ControlTilePlacement Clone() => new()
    {
        Type = Type,
        Column = Column,
        Row = Row,
        ColumnSpan = ColumnSpan,
        RowSpan = RowSpan
    };
}

public class ControlCenterModel
{
    public List<ControlTilePlacement> Layout2x2 { get; set; } = CreateDefault2x2();
    public List<ControlTilePlacement> Layout4x2 { get; set; } = CreateDefault4x2Horizontal();
    public List<ControlTilePlacement> Layout4x4 { get; set; } = CreateDefault4x4();

    /// <summary>Mouse wheel step percentage (1% - 10%) when hovering over Brightness / Volume / Mic sliders.</summary>
    public int WheelStep { get; set; } = 2;

    public List<ControlTilePlacement> GetLayoutForGrid(int cols, int rows)
    {
        if (cols <= 2 && rows <= 2)
        {
            if (Layout2x2 == null || Layout2x2.Count == 0)
                Layout2x2 = CreateDefault2x2();
            return NormalizeLayout(Layout2x2, 2, 2);
        }

        if (rows >= 3 && cols >= 3)
        {
            if (Layout4x4 == null || Layout4x4.Count == 0)
                Layout4x4 = CreateDefault4x4();
            return NormalizeLayout(Layout4x4, 4, 4);
        }

        if (Layout4x2 == null || Layout4x2.Count == 0)
            Layout4x2 = CreateDefault4x2Horizontal();
        return NormalizeLayout(Layout4x2, 4, 2);
    }

    public void SetLayoutForGrid(int cols, int rows, List<ControlTilePlacement> placements)
    {
        var cloned = placements.Select(p => p.Clone()).ToList();
        if (cols <= 2 && rows <= 2)
            Layout2x2 = NormalizeLayout(cloned, 2, 2);
        else if (rows >= 3 && cols >= 3)
            Layout4x4 = NormalizeLayout(cloned, 4, 4);
        else
            Layout4x2 = NormalizeLayout(cloned, 4, 2);
    }

    public static List<ControlTilePlacement> CreateDefault2x2() =>
    [
        new() { Type = ControlItemType.WiFi, Column = 0, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DarkMode, Column = 1, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Brightness, Column = 0, Row = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Volume, Column = 1, Row = 1, ColumnSpan = 1, RowSpan = 1 }
    ];

    public static List<ControlTilePlacement> CreateDefault4x2Horizontal() =>
    [
        new() { Type = ControlItemType.WiFi, Column = 0, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Bluetooth, Column = 1, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DarkMode, Column = 0, Row = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DesktopIcons, Column = 1, Row = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Brightness, Column = 2, Row = 0, ColumnSpan = 2, RowSpan = 1 },
        new() { Type = ControlItemType.Volume, Column = 2, Row = 1, ColumnSpan = 2, RowSpan = 1 }
    ];

    public static List<ControlTilePlacement> CreateDefault4x2Vertical() =>
    [
        new() { Type = ControlItemType.WiFi, Column = 0, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Bluetooth, Column = 1, Row = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DarkMode, Column = 0, Row = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DesktopIcons, Column = 1, Row = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.Brightness, Column = 2, Row = 0, ColumnSpan = 1, RowSpan = 2 },
        new() { Type = ControlItemType.Volume, Column = 3, Row = 0, ColumnSpan = 1, RowSpan = 2 }
    ];

    public static List<ControlTilePlacement> CreateDefault4x4() =>
    [
        new() { Type = ControlItemType.WiFi, Column = 0, Row = 0, ColumnSpan = 2, RowSpan = 1 },
        new() { Type = ControlItemType.Bluetooth, Column = 2, Row = 0, ColumnSpan = 2, RowSpan = 1 },
        new() { Type = ControlItemType.Brightness, Column = 0, Row = 1, ColumnSpan = 2, RowSpan = 1 },
        new() { Type = ControlItemType.Volume, Column = 2, Row = 1, ColumnSpan = 2, RowSpan = 1 },
        new() { Type = ControlItemType.AudioOutput, Column = 0, Row = 2, ColumnSpan = 2, RowSpan = 2 },
        new() { Type = ControlItemType.Microphone, Column = 2, Row = 2, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DarkMode, Column = 3, Row = 2, ColumnSpan = 1, RowSpan = 1 },
        new() { Type = ControlItemType.DesktopIcons, Column = 2, Row = 3, ColumnSpan = 2, RowSpan = 1 }
    ];

    public static List<ControlTilePlacement> NormalizeLayout(IEnumerable<ControlTilePlacement> input, int gridCols, int gridRows)
    {
        var result = new List<ControlTilePlacement>();
        var occupied = new bool[gridCols, gridRows];
        var seenTypes = new HashSet<ControlItemType>();

        foreach (var raw in input)
        {
            if (!seenTypes.Add(raw.Type))
                continue;

            int colSpan = Math.Clamp(raw.ColumnSpan, 1, Math.Min(2, gridCols));
            int rowSpan = Math.Clamp(raw.RowSpan, 1, Math.Min(2, gridRows));
            int col = Math.Clamp(raw.Column, 0, gridCols - colSpan);
            int row = Math.Clamp(raw.Row, 0, gridRows - rowSpan);

            if (CanFit(occupied, gridCols, gridRows, col, row, colSpan, rowSpan))
            {
                MarkOccupied(occupied, col, row, colSpan, rowSpan, true);
                result.Add(new ControlTilePlacement
                {
                    Type = raw.Type,
                    Column = col,
                    Row = row,
                    ColumnSpan = colSpan,
                    RowSpan = rowSpan
                });
            }
            else if (TryFindFirstFreeSlot(occupied, gridCols, gridRows, colSpan, rowSpan, out int freeCol, out int freeRow))
            {
                MarkOccupied(occupied, freeCol, freeRow, colSpan, rowSpan, true);
                result.Add(new ControlTilePlacement
                {
                    Type = raw.Type,
                    Column = freeCol,
                    Row = freeRow,
                    ColumnSpan = colSpan,
                    RowSpan = rowSpan
                });
            }
            else if (TryFindFirstFreeSlot(occupied, gridCols, gridRows, 1, 1, out int fallbackCol, out int fallbackRow))
            {
                MarkOccupied(occupied, fallbackCol, fallbackRow, 1, 1, true);
                result.Add(new ControlTilePlacement
                {
                    Type = raw.Type,
                    Column = fallbackCol,
                    Row = fallbackRow,
                    ColumnSpan = 1,
                    RowSpan = 1
                });
            }
        }

        return result;
    }

    public static bool CanFit(bool[,] occupied, int gridCols, int gridRows, int col, int row, int colSpan, int rowSpan)
    {
        if (col < 0 || row < 0 || colSpan < 1 || rowSpan < 1 || col + colSpan > gridCols || row + rowSpan > gridRows)
            return false;

        for (int c = col; c < col + colSpan; c++)
        {
            for (int r = row; r < row + rowSpan; r++)
            {
                if (occupied[c, r]) return false;
            }
        }
        return true;
    }

    public static void MarkOccupied(bool[,] occupied, int col, int row, int colSpan, int rowSpan, bool value)
    {
        for (int c = col; c < col + colSpan; c++)
        {
            for (int r = row; r < row + rowSpan; r++)
            {
                occupied[c, r] = value;
            }
        }
    }

    public static bool TryFindFirstFreeSlot(bool[,] occupied, int gridCols, int gridRows, int colSpan, int rowSpan, out int foundCol, out int foundRow)
    {
        for (int r = 0; r <= gridRows - rowSpan; r++)
        {
            for (int c = 0; c <= gridCols - colSpan; c++)
            {
                if (CanFit(occupied, gridCols, gridRows, c, r, colSpan, rowSpan))
                {
                    foundCol = c;
                    foundRow = r;
                    return true;
                }
            }
        }
        foundCol = 0;
        foundRow = 0;
        return false;
    }

    public static string GetItemDisplayName(ControlItemType type) => type switch
    {
        ControlItemType.Brightness => "屏幕亮度",
        ControlItemType.Volume => "系统音量",
        ControlItemType.AudioOutput => "音频输出设备",
        ControlItemType.Microphone => "麦克风静音",
        ControlItemType.WiFi => "无线网络 (Wi-Fi)",
        ControlItemType.Bluetooth => "蓝牙",
        ControlItemType.DarkMode => "系统深浅色",
        ControlItemType.DesktopIcons => "桌面图标显隐",
        _ => type.ToString()
    };

    public static string GetItemShortName(ControlItemType type) => type switch
    {
        ControlItemType.Brightness => "亮度",
        ControlItemType.Volume => "音量",
        ControlItemType.AudioOutput => "音频输出",
        ControlItemType.Microphone => "麦克风",
        ControlItemType.WiFi => "Wi-Fi",
        ControlItemType.Bluetooth => "蓝牙",
        ControlItemType.DarkMode => "深色模式",
        ControlItemType.DesktopIcons => "桌面图标",
        _ => type.ToString()
    };

    public static string GetItemIconPath(ControlItemType type, bool isActiveOrMuted = false, double level = 100) => type switch
    {
        ControlItemType.Brightness => "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58a.996.996 0 0 0-1.41 0 .996.996 0 0 0 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37a.996.996 0 0 0-1.41 0 .996.996 0 0 0 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0a.996.996 0 0 0 0-1.41l-1.06-1.06zm1.06-10.96a.996.996 0 0 0 0-1.41.996.996 0 0 0-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36a.996.996 0 0 0 0-1.41.996.996 0 0 0-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z",
        ControlItemType.Volume => isActiveOrMuted || level <= 0.5
            ? "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"
            : level < 50
                ? "M7 9v6h4l5 5V4l-5 5H7zm11.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z"
                : "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z",
        ControlItemType.AudioOutput => "M12 1a9 9 0 0 0-9 9v7c0 1.66 1.34 3 3 3h3v-8H5v-2c0-3.87 3.13-7 7-7s7 3.13 7 7v2h-4v8h3c1.66 0 3-1.34 3-3v-7a9 9 0 0 0-9-9z",
        ControlItemType.Microphone => isActiveOrMuted
            ? "M19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23c.56-.98.9-2.09.9-3.28zm-4.02.17c0-.06.02-.11.02-.17V5c0-1.66-1.34-3-3-3S9 3.34 9 5v.18l5.98 5.99zM4.27 3L3 4.27l6.01 6.01V11c0 1.66 1.33 3 2.99 3 .22 0 .44-.03.65-.08l1.66 1.66c-.71.33-1.5.52-2.31.52-2.76 0-5.3-2.1-5.3-5.1H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c.91-.13 1.77-.45 2.54-.9L19.73 21 21 19.73 4.27 3z"
            : "M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5.91-3c-.49 0-.9.36-.98.85C16.52 14.2 14.47 16 12 16s-4.52-1.8-4.93-4.15c-.08-.49-.49-.85-.98-.85-.61 0-1.09.54-1 1.14.49 3 2.89 5.35 5.91 5.78V21h2v-3.08c3.02-.43 5.42-2.78 5.91-5.78.1-.6-.39-1.14-1-1.14z",
        ControlItemType.WiFi => "M1 9l2 2c4.97-4.97 13.03-4.97 18 0l2-2C16.93 2.93 7.08 2.93 1 9zm8 8l3 3 3-3c-1.65-1.66-4.34-1.66-6 0zm-4-4l2 2c2.76-2.76 7.24-2.76 10 0l2-2C15.14 9.14 8.87 9.14 5 13z",
        ControlItemType.Bluetooth => "M17.71 7.71L12 2h-1v7.59L6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 11 14.41V22h1l5.71-5.71-4.3-4.29 4.3-4.29zM13 5.83l1.88 1.88L13 9.59V5.83zm1.88 10.46L13 18.17v-3.76l1.88 1.88z",
        ControlItemType.DarkMode => isActiveOrMuted
            ? "M12 3a9 9 0 1 0 9 9c0-.46-.04-.92-.1-1.36a5.389 5.389 0 0 1-4.4 2.26 5.403 5.403 0 0 1-3.14-9.8c-.44-.06-.9-.1-1.36-.1z"
            : "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1z",
        ControlItemType.DesktopIcons => isActiveOrMuted
            ? "M4 6h16v10H4V6zm16-2H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h6v2H8v2h8v-2h-2v-2h6c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zM6 8h2v2H6V8zm0 3h2v2H6v-2zm3-3h2v2H9V8z"
            : "M20 4H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h6v2H8v2h8v-2h-2v-2h6c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 12H4V6h16v10z",
        _ => "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2z"
    };
}
