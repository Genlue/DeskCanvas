using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DeskCanvas.Services;
using TickClock.Models;

namespace TickClock.ViewModels;

/// <summary>
/// 刻度时钟的数据源：1 秒滴答一次（分钟显示必须踩在整分上，
/// 共享的 1 分钟定时器从订阅时刻起算，最坏会滞后 59 秒），
/// 但只有格式化后的文本真的变化时才发通知，避免每秒无谓重排。
/// </summary>
public class TickClockViewModel : INotifyPropertyChanged, IDisposable
{
    private TickClockModel model;
    private bool isRunning;

    private string timeText = string.Empty;
    private string weekdayText = string.Empty;
    private string dateText = string.Empty;
    /// <summary>全彩主题下“周X”的固定朱红，不随设置变化。</summary>
    private static readonly IBrush WeekdayAccentBrush = new SolidColorBrush(Color.Parse(TickClockModel.AccentColor));

    public TickClockViewModel(TickClockModel? initialModel = null)
    {
        model = initialModel ?? new TickClockModel();
        Update(force: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TickClockModel Model => model;

    public bool ShowWeekday => model.ShowWeekday;

    public bool ShowDate => model.ShowDate;

    public bool Use24Hour => model.Use24Hour;

    public string TimeText
    {
        get => timeText;
        private set => SetField(ref timeText, value);
    }

    public string WeekdayText
    {
        get => weekdayText;
        private set => SetField(ref weekdayText, value);
    }

    public string DateText
    {
        get => dateText;
        private set => SetField(ref dateText, value);
    }

    public IBrush AccentBrush => WeekdayAccentBrush;

    /// <summary>换模型（设置页保存后宿主回调 <c>Refresh</c>）：重算颜色与文本。</summary>
    public void Apply(TickClockModel updated)
    {
        model = updated;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowWeekday)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Use24Hour)));

        Update(force: true);
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        // 订阅的是共享定时器：全屏应用期间的 PauseAll/ResumeAll 自动生效。
        TimerService.Timer1Second.Subscribe(Tick);
        Update(force: true);
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        TimerService.Timer1Second.Unsubscribe(Tick);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Tick() => Update();

    /// <summary>
    /// 取当前本地时间并刷新文本。文本没变时一个通知都不发——
    /// 一秒一次的 Tick 在 59/60 的时间里都会走到这条早退分支。
    /// </summary>
    private void Update(bool force = false)
    {
        var now = DateTime.Now;

        string time = model.Use24Hour
            ? now.ToString("HH:mm", CultureInfo.InvariantCulture)
            : now.ToString("h:mm", CultureInfo.InvariantCulture);
        string weekday = FormatWeekday(now);
        // 六分之一空格（U+2006）而不是普通空格：普通空格在 58 号字下会让“10”和“.”拉开
        // 近 20px，参考图里只有一半左右。
        string date = $"{now.Month}\u2006.\u2006{now.Day}";

        if (force || time != TimeText) TimeText = time;
        if (force || weekday != WeekdayText) WeekdayText = weekday;
        if (force || date != DateText) DateText = date;
    }

    /// <summary>
    /// 中文取“周二”，其他语言取大写三字母缩写（Tue → TUE）。
    /// 用当前 UI 区域而不是硬编码，跟随宿主语言。
    /// </summary>
    private static string FormatWeekday(DateTime now)
    {
        var culture = CultureInfo.CurrentUICulture;
        string name;
        try
        {
            name = culture.DateTimeFormat.AbbreviatedDayNames[(int)now.DayOfWeek];
        }
        catch
        {
            name = now.DayOfWeek.ToString();
        }

        if (string.IsNullOrEmpty(name)) name = now.DayOfWeek.ToString();
        return name.Length <= 3 ? name.ToUpper(culture) : name;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
