using System;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Services;

/// <summary>Apply a single screen edit to current storage, never to a UI snapshot.</summary>
public static class ScreenConfigEditor
{
    public static void Save(ILayoutProvider provider, string screenId, Func<ScreenLayout, ScreenLayout> update)
    {
        var screens = provider.Get();
        var current = screens.FindById(screenId);
        if (current == null) return;
        provider.Save(screens.WithScreen(update(current)));
    }
}
