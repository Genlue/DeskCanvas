using System;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;

/// <summary>
/// Builds a display object with arbitrary metrics for the checks.
/// <para>
/// Avalonia 12 made <see cref="Screen"/> abstract and gave it a <c>private protected</c> constructor,
/// so it can neither be instantiated nor subclassed outside Avalonia — <c>new Screen(...)</c> is gone
/// for good. <c>Avalonia.Platform.PlatformScreen</c> is the only constructible implementation (public
/// in the runtime assembly, absent from the reference assembly), and its metrics are set through
/// non-public setters, hence the reflection.
/// </para>
/// </summary>
internal static class TestScreen
{
    private sealed class FakeHandle : IPlatformHandle
    {
        public static readonly FakeHandle Instance = new();
        public IntPtr Handle => IntPtr.Zero;
        public string HandleDescriptor => "TEST";
    }

    public static Screen Create(double scaling, PixelRect bounds, PixelRect workingArea, bool isPrimary)
    {
        var type = typeof(Screen).Assembly.GetType("Avalonia.Platform.PlatformScreen")
                   ?? throw new InvalidOperationException("Avalonia.Platform.PlatformScreen is unavailable in this Avalonia build.");

        var screen = (Screen)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [FakeHandle.Instance],
            culture: null)!;

        Set(screen, nameof(Screen.Bounds), bounds);
        Set(screen, nameof(Screen.WorkingArea), workingArea);
        Set(screen, nameof(Screen.Scaling), scaling);
        Set(screen, nameof(Screen.IsPrimary), isPrimary);
        return screen;
    }

    private static void Set<T>(Screen screen, string property, T value) =>
        typeof(Screen).GetProperty(property)!.SetValue(screen, value);
}
