using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ReactiveUI;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace DeskCanvas.ViewModels;

public class ProfileItemViewModel : ReactiveObject
{
    private string name;
    public string Name
    {
        get => name;
        set => this.RaiseAndSetIfChanged(ref name, value);
    }

    private bool isActive;
    public bool IsActive
    {
        get => isActive;
        set
        {
            this.RaiseAndSetIfChanged(ref isActive, value);
            this.RaisePropertyChanged(nameof(CanSwitch));
            this.RaisePropertyChanged(nameof(CanDelete));
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    private int totalCount;
    public int TotalCount
    {
        get => totalCount;
        set
        {
            this.RaiseAndSetIfChanged(ref totalCount, value);
            this.RaisePropertyChanged(nameof(CanDelete));
        }
    }

    public bool CanSwitch => !IsActive;
    public bool CanDelete => !IsActive && TotalCount > 1;

    public string StatusText => IsActive ? (Locale.Profiles_CurrentActive ?? "使用中") : "";

    public string LastModified { get; set; } = "";

    public ProfileItemViewModel(string name, bool isActive, int totalCount, string lastModified = "")
    {
        this.name = name;
        this.isActive = isActive;
        this.totalCount = totalCount;
        this.LastModified = lastModified;
    }
}

public class ProfilesViewModel : ReactiveObject
{
    private readonly ProfileService profileService;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    private string activeProfileName = "";
    public string ActiveProfileName
    {
        get => activeProfileName;
        private set => this.RaiseAndSetIfChanged(ref activeProfileName, value);
    }

    public ProfilesViewModel(ProfileService profileService)
    {
        this.profileService = profileService;
        profileService.ActiveProfileChanged += OnServiceChanged;
        profileService.ProfilesListChanged += OnServiceChanged;

        Refresh();
    }

    private void OnServiceChanged(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        ActiveProfileName = profileService.GetActiveProfile();
        var allNames = profileService.GetProfiles();
        int total = allNames.Count;

        Profiles.Clear();
        foreach (var name in allNames)
        {
            var isAct = string.Equals(name, ActiveProfileName, StringComparison.OrdinalIgnoreCase);
            string modStr = "";
            try
            {
                var p = profileService.GetProfilePath(name);
                if (File.Exists(p))
                {
                    modStr = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { }

            Profiles.Add(new ProfileItemViewModel(name, isAct, total, modStr));
        }
    }

    public void SwitchProfile(ProfileItemViewModel item)
    {
        if (item == null || item.IsActive) return;
        profileService.SwitchProfile(item.Name);
    }

    public async Task CreateProfileAsync(Window owner)
    {
        var newName = await InputDialog.PromptAsync(
            owner,
            Locale.Profiles_New_Title ?? "新建配置方案",
            Locale.Profiles_New_Prompt ?? "请输入新方案名称：",
            "新方案",
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? "方案名称不能为空";
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? "方案名称包含非法字符";
                if (profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? "该方案名称已存在";
                return null;
            });

        if (!string.IsNullOrWhiteSpace(newName))
        {
            if (profileService.CreateProfile(newName, copyCurrent: true))
            {
                // Switch to newly created profile
                profileService.SwitchProfile(newName);
            }
        }
    }

    public async Task RenameProfileAsync(ProfileItemViewModel item, Window owner)
    {
        if (item == null) return;

        var newName = await InputDialog.PromptAsync(
            owner,
            Locale.Profiles_Rename_Title ?? "重命名配置方案",
            Locale.Profiles_Rename_Prompt ?? "请输入新名称：",
            item.Name,
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? "方案名称不能为空";
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? "方案名称包含非法字符";
                if (!string.Equals(name.Trim(), item.Name, StringComparison.OrdinalIgnoreCase) &&
                    profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? "该方案名称已存在";
                return null;
            });

        if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            profileService.RenameProfile(item.Name, newName);
        }
    }

    public async Task DeleteProfileAsync(ProfileItemViewModel item, Window owner)
    {
        if (item == null || item.IsActive || Profiles.Count <= 1) return;

        var confirmMsg = string.Format(
            Locale.Profiles_Delete_Confirm ?? "确定要删除配置方案“{0}”吗？此操作无法撤销。",
            item.Name);

        var ok = await ConfirmDialog.ConfirmAsync(
            owner,
            confirmMsg,
            Locale.Profiles_Delete ?? "删除",
            Locale.Cancel ?? "取消");

        if (ok)
        {
            profileService.DeleteProfile(item.Name);
        }
    }

    public async Task DuplicateProfileAsync(ProfileItemViewModel item, Window owner)
    {
        if (item == null) return;

        var defaultCopyName = $"{item.Name}_副本";
        var newName = await InputDialog.PromptAsync(
            owner,
            Locale.Profiles_Duplicate ?? "复制配置方案",
            Locale.Profiles_New_Prompt ?? "请输入方案名称：",
            defaultCopyName,
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? "方案名称不能为空";
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? "方案名称包含非法字符";
                if (profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? "该方案名称已存在";
                return null;
            });

        if (!string.IsNullOrWhiteSpace(newName))
        {
            profileService.DuplicateProfile(item.Name, newName);
        }
    }

    public async Task ExportProfileAsync(ProfileItemViewModel item, Window owner)
    {
        if (item == null) return;

        var storage = owner.StorageProvider;
        if (storage == null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Locale.Profiles_Export ?? "导出配置方案",
            SuggestedFileName = $"{item.Name}.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });

        if (file != null)
        {
            try
            {
                var localPath = file.Path.LocalPath;
                profileService.ExportProfile(item.Name, localPath);
            }
            catch (Exception ex)
            {
                await ConfirmDialog.InformAsync(owner, $"导出失败: {ex.Message}", "确定");
            }
        }
    }

    public async Task ImportProfileAsync(Window owner)
    {
        var storage = owner.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.Profiles_Import ?? "导入配置方案",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });

        if (files.Count > 0)
        {
            try
            {
                var localPath = files[0].Path.LocalPath;
                var importedName = profileService.ImportProfile(localPath);
                var switchNow = await ConfirmDialog.ConfirmAsync(
                    owner,
                    $"已成功导入方案“{importedName}”。是否立即切换到该方案？",
                    "立即切换",
                    "稍后切换");
                if (switchNow)
                {
                    profileService.SwitchProfile(importedName);
                }
            }
            catch (Exception ex)
            {
                await ConfirmDialog.InformAsync(owner, $"导入失败: {ex.Message}", "确定");
            }
        }
    }
}
