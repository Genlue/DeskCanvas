using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Reminders.ViewModels;

namespace Reminders.Views.Controls;

public partial class ListWide : UserControl
{
    private readonly List owner;
    private readonly RemindersViewModel viewModel;

    public ListWide(List owner, RemindersViewModel viewModel, bool compact = false)
    {
        this.owner = owner;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // 一级界面编辑开关：卡片根类 editable 放行 AllowEdit（默认禁用），
        // 设置翻转即时生效，无需重建卡片。
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ApplyEditable();

        // M tier (2×1): the 90px-wide name column leaves almost nothing for the
        // items — hide the name and shrink the counter to a small badge.
        if (compact)
        {
            ListNameBox.IsVisible = false;
            CountText.FontSize = 20;
            Margin = new Thickness(8, 2, 4, 0);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemindersViewModel.AllowInlineEdit))
            ApplyEditable();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // 卡片按跨度重建时会替换实例：旧实例退出，别挂在 viewModel 上泄漏。
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>卡片编辑开关：类切换驱动静态样式（AllowEdit 无绑定、无优先级之争）。</summary>
    private void ApplyEditable()
    {
        if (viewModel.AllowInlineEdit) Classes.Add("editable");
        else Classes.Remove("editable");
    }

    public void ListNameChanged(object? sender, RoutedEventArgs e) => owner.ListNameChanged(sender, e);
    public void CompleteReminder(object? sender, RoutedEventArgs e) => owner.CompleteReminder(sender, e);
    public void EditReminder(object? sender, RoutedEventArgs e) => owner.EditReminder(sender, e);
    public void CreateReminder(object? sender, RoutedEventArgs e) => owner.CreateReminder(sender, e);
    public void OpenPopup(object? sender, RoutedEventArgs e) => owner.OpenPopupWindow();
}
