using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shortcuts.Services;

/// <summary>
/// Native Windows file / folder picker (IFileOpenDialog) and saver (IFileSaveDialog), the same
/// approach the Folders widget uses — the OS dialog is far more reliable inside this
/// desktop-widget app than the managed storage provider, and FOS_NODEREFERENCELINKS keeps .lnk
/// paths intact (needed by "Open App").
/// Returns null when the user cancels or the dialog cannot open.
/// </summary>
public static class FileDialogs
{
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr parent);
        void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);
        void SetFileTypeIndex([In] uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise([In, MarshalAs(UnmanagedType.Interface)] IntPtr pfde, out uint pdwCookie);
        void Unadvise([In] uint dwCookie);
        void SetOptions([In] uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IntPtr psi);
        void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IntPtr psi);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IntPtr ppsi);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IntPtr ppsi);
        void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IntPtr psi, int fdap);
        void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
        void GetResults([MarshalAs(UnmanagedType.Interface)] out IntPtr ppenum);
        void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass { }

    // The save dialog CLSID. IFileSaveDialog extends IFileOpenDialog, and we only need the
    // vtable-prefix members (Show/SetOptions/SetFileName/SetTitle/SetDefaultExtension/
    // GetResult), so the full IFileSaveDialog interface is never declared — the existing
    // IFileOpenDialog declaration (above) is cast-compatible for those calls.
    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogCoClass { }

    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_NODEREFERENCELINKS = 0x00100000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>
    /// Test seam (set only by the headless check harnesses): when non-null, picking is delegated
    /// to it instead of opening the real dialog. Signature: (pickFolder, title) -> path or null.
    /// </summary>
    public static Func<bool, string?, string?>? TestOverride { get; set; }

    /// <summary>
    /// Test seam for the save dialog (headless check harnesses only): when non-null,
    /// <see cref="PutFile"/> delegates to it instead of opening the real dialog. Signature:
    /// (defaultName) -> full save path or null.
    /// </summary>
    public static Func<string?, string?>? TestSaveOverride { get; set; }

    /// <summary>Pick a file (shortcuts are not dereferenced); null on cancel.</summary>
    public static string? PickFile(IntPtr ownerHwnd, string? title = null) =>
        TestOverride?.Invoke(false, title) ?? Show(ownerHwnd, title, pickFolder: false);

    /// <summary>Pick a folder; null on cancel.</summary>
    public static string? PickFolder(IntPtr ownerHwnd, string? title = null) =>
        TestOverride?.Invoke(true, title) ?? Show(ownerHwnd, title, pickFolder: true);

    /// <summary>
    /// Native save dialog: asks for a full file path to write to. <paramref name="defaultName"/>
    /// seeds the file name box, <paramref name="defaultExtension"/> is appended when the user
    /// omits one, and the shell handles the overwrite prompt. Null on cancel or failure.
    /// </summary>
    public static string? PutFile(IntPtr ownerHwnd, string? title, string? defaultName, string? defaultExtension)
    {
        if (TestSaveOverride != null) return TestSaveOverride(defaultName);

        try
        {
            // Same interface shape as the open dialog (IFileSaveDialog is a strict superset).
            var dialog = (IFileOpenDialog)new FileSaveDialogCoClass();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_OVERWRITEPROMPT | FOS_FORCEFILESYSTEM);
            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);
            if (!string.IsNullOrEmpty(defaultName))
                dialog.SetFileName(defaultName);
            if (!string.IsNullOrEmpty(defaultExtension))
                dialog.SetDefaultExtension(defaultExtension);

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0) return null; // cancelled or failed

            dialog.GetResult(out var item);
            if (item == null) return null;

            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shortcuts.FileDialogs] Native save dialog failed: {ex.Message}");
            return null;
        }
    }

    private static string? Show(IntPtr ownerHwnd, string? title, bool pickFolder)
    {
        try
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            dialog.GetOptions(out var options);
            var flags = options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
            flags |= pickFolder ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST | FOS_NODEREFERENCELINKS;
            dialog.SetOptions(flags);
            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            var hr = dialog.Show(ownerHwnd);
            if (hr != 0) return null; // cancelled or failed

            dialog.GetResult(out var item);
            if (item == null) return null;

            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shortcuts.FileDialogs] Native picker failed: {ex.Message}");
            return null;
        }
    }
}
