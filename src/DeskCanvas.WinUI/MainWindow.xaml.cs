using System;

namespace DeskCanvas.WinUI;

/// <summary>
/// 诊断窗口：只在 <c>--winui-diagnostic</c> 时显示，正常启动不出现。
/// 标题只放产品名，正文只放启动阶段名（内部诊断 token），不构成需要本地化的用户文案。
/// </summary>
public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetStage(string stage)
    {
        if (string.IsNullOrEmpty(stage)) return;

        StatusText.Text = stage;
        StageText.Text += stage + Environment.NewLine;
    }
}
