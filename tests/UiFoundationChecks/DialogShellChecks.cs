using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using DialogShell = DeskCanvas.UI.Controls.DialogShell;

namespace UiFoundationChecks;

/// <summary>
/// Checks for the shared dialog chrome (<see cref="HostDialogWindow"/> + <see cref="DialogShell"/>).
/// <list type="bullet">
/// <item><b>One chrome.</b> A dialog window is transparent around its card: no
/// <c>AcrylicBlur</c> transparency level (that put the card inside a blurred window — the reported
/// "弹窗外围多一圈毛玻璃"), <c>WindowDecorations=None</c>, and the card is inset by the shell's
/// shadow gutter, which must stay transparent because it only carries the shadow.</item>
/// <item><b>Movable.</b> The header strip carries the shared drag handle, and a real left press on
/// its empty area runs the drag path without throwing. A headerless alert hands the same contract
/// to the card, while its buttons stay excluded. This is the reported "缩放比例弹窗没法移动": the
/// old window asked the OS for an extended caption, so the press never reached its own handler.</item>
/// <item><b>One registration.</b> The role comes from the base class, so a dialog never stacks a
/// second <c>WindowRoles.Register</c> (the registry is not idempotent).</item>
/// <item><b>Host surface contract.</b> The card fill / hairline resolve from the app's menu-surface
/// family through the host style — a dialog is a transient surface over the desktop, not a third
/// palette, and it follows light/dark without naming a colour.</item>
/// </list>
/// Headless-environment limits (asserted by humans, never faked here): the native drag itself
/// (BeginMoveDrag needs a real HWND), the drop shadow's appearance and the transparent window's
/// DWM composition.
/// </summary>
internal static partial class Program
{
    private static void CheckDialogShell()
    {
        Console.WriteLine("--- dialog chrome: DialogShell + HostDialogWindow ---");

        // ---- 1. 缩放比例: the reported dialog, on the shared chrome -------------------------
        var scale = new ScaleDialog(1.25, _ => { });
        scale.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Check(scale.IsVisible, "ScaleDialog opens through the shared dialog base");
        Check(WindowRoles.GetRole(scale) == WindowRole.Dialog, "the dialog role comes from the base class");
        Check(scale.TransparencyLevelHint is { Count: > 0 } hints
              && hints.All(hint => hint == WindowTransparencyLevel.Transparent),
            $"the window asks for a transparent frame only " +
            $"({string.Join(",", scale.TransparencyLevelHint.Select(h => h.ToString()))})");
        Check(scale.WindowDecorations == WindowDecorations.None,
            "no OS border is drawn around the card");

        var shell = scale.Content as DialogShell;
        Check(shell is not null, "the content root is the shared DialogShell");
        if (shell is not null)
        {
            Check(shell.IsTemplateApplied && shell.CardBorder is not null,
                "the shell's template applied (card + header + content parts resolve)");

            // The gutter is the transparent room the shadow lives in. A card that filled the
            // window would have nothing to cast a shadow into, and an acrylic window under it is
            // exactly the frosted ring this shell replaces.
            Check(shell.CardBorder!.Margin.Equals(shell.ShadowGutter) && shell.ShadowGutter != default,
                $"the card is inset by the shadow gutter, not by a second surface ({shell.CardBorder.Margin})");
            Check(shell.CardBackground is ISolidColorBrush { Color.A: > 0 },
                "the host dialog style paints the card fill (menu-surface family)");
            Check(shell.CardBorderBrush is not null, "the host dialog style paints the card hairline");
            Check(shell.HasHeader, "a titled dialog paints its header");

            // The header is the drag handle: a press in its empty right-hand area must run the
            // shared drag path. On the old window this press was swallowed by the extended caption
            // area, which is why the dialog could not be moved at all.
            var header = shell.HeaderDragArea;
            Check(header is not null && PanelDragBehavior.GetIsDragHandle(header),
                "the header strip is the shared drag handle");
            if (header is not null)
            {
                var origin = header.TranslatePoint(default, scale) ?? default;
                var empty = new Point(origin.X + header.Bounds.Width * 0.5, origin.Y + header.Bounds.Height / 2);
                var hit = scale.GetVisualAt(empty);
                Check(hit is not null && (ReferenceEquals(hit, header) || hit.GetVisualAncestors().Contains(header)),
                    $"empty title space belongs to the handle strip (hit {hit?.GetType().Name ?? "null"})");

                scale.MouseDown(empty, MouseButton.Left);
                scale.MouseUp(empty, MouseButton.Left);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Check(scale.IsVisible, "a left press on the empty header runs the drag path cleanly");
            }
        }

        // The reported dialog also has to keep its two actions and its field.
        Check(scale.GetVisualDescendants().OfType<TextBox>().Any(),
            "the scale field survives the migration");
        Check(scale.GetVisualDescendants().OfType<Button>().Count() >= 2,
            "both actions (恢复默认 / 确定) survive the migration");
        scale.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // ---- 2. A headerless alert: the card is the handle --------------------------------
        var confirm = new ConfirmDialog();
        confirm.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        if (confirm.Content is DialogShell alert)
        {
            Check(!alert.HasHeader, "a message-only alert paints no empty header strip");
            Check(alert.CardBorder is not null && PanelDragBehavior.GetIsDragHandle(alert.CardBorder),
                "…and hands the drag contract to the card itself");
            Check(!PanelDragBehavior.GetIsDragHandle(alert.HeaderDragArea!),
                "the collapsed header is never a second drag handle");
        }
        else
        {
            Check(false, "ConfirmDialog uses the shared DialogShell");
        }

        var confirmButton = confirm.FindControl<Button>("ConfirmButton")
                            ?? confirm.GetVisualDescendants().OfType<Button>().FirstOrDefault();
        Check(confirmButton is not null, "the confirm action is still reachable after the migration");
        if (confirmButton is not null && confirm.Content is DialogShell dragShell)
        {
            Check(PanelDragBehavior.IsDragExcluded(confirmButton, dragShell.CardBorder!),
                "a press on the alert's action is excluded from the card drag");
        }

        confirm.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // ---- 3. HasHeader follows its four inputs -----------------------------------------
        var probe = new DialogShell();
        Check(!probe.HasHeader, "a bare shell has no header");
        probe.Title = "Title";
        Check(probe.HasHeader, "setting a title reveals the header");
        probe.Title = null;
        probe.HeaderActions = new Button();
        Check(probe.HasHeader, "header actions alone keep the header (and its drag area) alive");

        // ---- 4. The surface contract follows the variant ---------------------------------
        // Two explicit variants: the fill must come from the app's variant dictionaries, so a
        // dialog never has to name a colour (and the monochrome/降级 variants get it for free).
        var light = new ScaleDialog(1.0, _ => { }) { RequestedThemeVariant = ThemeVariant.Light };
        var dark = new ScaleDialog(1.0, _ => { }) { RequestedThemeVariant = ThemeVariant.Dark };
        light.Show();
        dark.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var lightFill = (light.Content as DialogShell)?.CardBackground as ISolidColorBrush;
        var darkFill = (dark.Content as DialogShell)?.CardBackground as ISolidColorBrush;
        Check(lightFill is not null && darkFill is not null && lightFill.Color != darkFill.Color,
            $"the card fill follows the variant (light {lightFill?.Color} vs dark {darkFill?.Color})");

        light.Close();
        dark.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
