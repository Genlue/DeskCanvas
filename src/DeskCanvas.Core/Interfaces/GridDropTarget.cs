namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// A drop target plus the one fact the preview and the commit both have to agree on: whether the
/// cells the card would land on are already taken by a <b>different</b> widget on the target
/// screen.
/// <para>
/// <see cref="Blocked"/> is only ever <c>true</c> while 禁止组件重叠
/// (<c>Layout.ProhibitOverlap</c>) is on. With the setting off the answer is always <c>false</c>
/// and the drag behaves exactly as it did before the setting existed — the card may be dropped on
/// top of another one (从左上角开始叠).
/// </para>
/// <para>
/// The two halves travel together because they are computed from the same resolve: a caller that
/// asked separately would be free to preview one rect and commit another, which is exactly how an
/// outline ends up promising a cell the release then refuses.
/// </para>
/// </summary>
/// <param name="Target">The rectangle the release would snap the window to (physical pixels).</param>
/// <param name="Blocked">Whether that rectangle overlaps another widget's stored cell on the
/// target screen.</param>
public readonly record struct GridDropTarget(GridTarget Target, bool Blocked);
