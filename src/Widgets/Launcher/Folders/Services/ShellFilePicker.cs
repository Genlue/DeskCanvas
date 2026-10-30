using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Folders.Services;

/// <summary>
/// Native Windows file picker that respects shortcuts (.lnk) without dereferencing them.
/// Uses IFileOpenDialog with FOS_NODEREFERENCELINKS so the selected .lnk file path is preserved.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellFilePicker
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

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, [In] ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass { }

    private const uint FOS_NODEREFERENCELINKS = 0x00100000;
    private const uint FOS_FORCEFILESYSTEM    = 0x00000040;
    private const uint FOS_FILEMUSTEXIST      = 0x00001000;
    private const uint FOS_PATHMUSTEXIST      = 0x00000800;
    private const uint FOS_ALLOWMULTISELECT   = 0x00000200;
    private const uint SIGDN_FILESYSPATH      = 0x80058000;

    /// <summary>
    /// Open Windows native file dialog without dereferencing shortcuts (.lnk).
    /// If the user selects a shortcut, returns the .lnk path itself rather than resolving to its target.
    /// </summary>
    public static string? PickSingleFileNoDereference(IntPtr ownerHwnd, string? title = null)
    {
        var files = PickFilesNoDereference(ownerHwnd, title, allowMultiple: false);
        return files.Count > 0 ? files[0] : null;
    }

    /// <summary>
    /// Open Windows native file dialog for multiple files without dereferencing shortcuts (.lnk).
    /// </summary>
    public static List<string> PickFilesNoDereference(IntPtr ownerHwnd, string? title = null, bool allowMultiple = false)
    {
        var results = new List<string>();
        try
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            dialog.GetOptions(out uint options);
            uint flags = options | FOS_NODEREFERENCELINKS | FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST;
            if (allowMultiple)
                flags |= FOS_ALLOWMULTISELECT;
            dialog.SetOptions(flags);
            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            var hr = dialog.Show(ownerHwnd);
            if (hr == 0) // S_OK
            {
                if (allowMultiple)
                {
                    dialog.GetResults(out var resultsPtr);
                    if (resultsPtr != IntPtr.Zero)
                    {
                        var array = (IShellItemArray)Marshal.GetObjectForIUnknown(resultsPtr);
                        array.GetCount(out uint count);
                        for (uint i = 0; i < count; i++)
                        {
                            array.GetItemAt(i, out var item);
                            if (item != null)
                            {
                                item.GetDisplayName(SIGDN_FILESYSPATH, out var p);
                                if (!string.IsNullOrEmpty(p))
                                    results.Add(p);
                            }
                        }
                    }
                }
                else
                {
                    dialog.GetResult(out var item);
                    if (item != null)
                    {
                        item.GetDisplayName(SIGDN_FILESYSPATH, out var p);
                        if (!string.IsNullOrEmpty(p))
                            results.Add(p);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellFilePicker] Native picker failed: {ex.Message}");
        }
        return results;
    }
}
