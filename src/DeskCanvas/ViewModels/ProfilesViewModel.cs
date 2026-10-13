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

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
        "zh", StringComparison.OrdinalIgnoreCase);

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
                    var dt = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm");
                    modStr = IsZh ? $"修改时间：{dt}" : $"Modified: {dt}";
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
            Locale.Profiles_New_Title ?? (IsZh ? "新建配置方案" : "Create Profile"),
            Locale.Profiles_New_Prompt ?? (IsZh ? "请输入新方案名称：" : "Enter profile name:"),
            Locale.Profiles_New ?? (IsZh ? "新方案" : "New Profile"),
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称不能为空" : "Profile name cannot be empty");
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称包含非法字符" : "Profile name contains invalid characters");
                if (profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? (IsZh ? "该方案名称已存在" : "Profile name already exists");
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
            Locale.Profiles_Rename_Title ?? (IsZh ? "重命名配置方案" : "Rename Profile"),
            Locale.Profiles_Rename_Prompt ?? (IsZh ? "请输入新名称：" : "Enter new name:"),
            item.Name,
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称不能为空" : "Profile name cannot be empty");
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称包含非法字符" : "Profile name contains invalid characters");
                if (!string.Equals(name.Trim(), item.Name, StringComparison.OrdinalIgnoreCase) &&
                    profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? (IsZh ? "该方案名称已存在" : "Profile name already exists");
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
            Locale.Profiles_Delete_Confirm ?? (IsZh ? "确定要删除配置方案“{0}”吗？此操作无法撤销。" : "Are you sure you want to delete profile \"{0}\"? This cannot be undone."),
            item.Name);

        var ok = await ConfirmDialog.ConfirmAsync(
            owner,
            confirmMsg,
            Locale.Profiles_Delete ?? (IsZh ? "删除" : "Delete"),
            Locale.Cancel ?? (IsZh ? "取消" : "Cancel"));

        if (ok)
        {
            profileService.DeleteProfile(item.Name);
        }
    }

    public async Task DuplicateProfileAsync(ProfileItemViewModel item, Window owner)
    {
        if (item == null) return;

        var defaultCopyName = IsZh ? $"{item.Name}_副本" : $"{item.Name}_Copy";
        var newName = await InputDialog.PromptAsync(
            owner,
            Locale.Profiles_Duplicate ?? (IsZh ? "复制配置方案" : "Duplicate Profile"),
            Locale.Profiles_New_Prompt ?? (IsZh ? "请输入方案名称：" : "Enter profile name:"),
            defaultCopyName,
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称不能为空" : "Profile name cannot be empty");
                var invalid = Path.GetInvalidFileNameChars();
                if (name.Any(c => invalid.Contains(c)))
                    return Locale.Profiles_NameInvalid ?? (IsZh ? "方案名称包含非法字符" : "Profile name contains invalid characters");
                if (profileService.GetProfiles().Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Locale.Profiles_NameExists ?? (IsZh ? "该方案名称已存在" : "Profile name already exists");
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
            Title = Locale.Profiles_Export ?? (IsZh ? "导出配置方案" : "Export Profile"),
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
                await ConfirmDialog.InformAsync(
                    owner,
                    IsZh ? $"导出失败: {ex.Message}" : $"Export failed: {ex.Message}",
                    Locale.Settings_Advanced_Backup_OkButton ?? (IsZh ? "确定" : "OK"));
            }
        }
    }

    public async Task ImportProfileAsync(Window owner)
    {
        var storage = owner.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.Profiles_Import ?? (IsZh ? "导入配置方案" : "Import Profile"),
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
                    IsZh
                        ? $"已成功导入方案“{importedName}”。是否立即切换到该方案？"
                        : $"Imported profile \"{importedName}\". Switch to it now?",
                    IsZh ? "立即切换" : "Switch Now",
                    IsZh ? "稍后切换" : "Later");
                if (switchNow)
                {
                    profileService.SwitchProfile(importedName);
                }
            }
            catch (Exception ex)
            {
                await ConfirmDialog.InformAsync(
                    owner,
                    IsZh ? $"导入失败: {ex.Message}" : $"Import failed: {ex.Message}",
                    Locale.Settings_Advanced_Backup_OkButton ?? (IsZh ? "确定" : "OK"));
            }
        }
    }
}
