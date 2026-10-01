using System;
using System.Collections.Generic;
using Avalonia.Media;
using DeskCanvas.Core.Models;

namespace DeskCanvas.ViewModels;

public record PageViewModel(
    Type? Type, 
    StreamGeometry? Icon, 
    string Text, 
    AssemblyInfo? AssemblyInfo = null,
    IEnumerable<AssemblyInfo>? AssemblyInfos = null)
{
    public bool IsHeader => Type == null && !string.IsNullOrEmpty(Text);
    public bool IsSeparator => Type == null && string.IsNullOrEmpty(Text);
    public bool IsItem => Type != null;
    public bool IsNotSeparator => !IsSeparator;
}