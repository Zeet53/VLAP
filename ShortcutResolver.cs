using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VLAP;

/// <summary>Reads .lnk and .url files to find what they actually point at, instead of keeping the wrapper.</summary>
public static class ShortcutResolver
{
    /// <summary>Target path, arguments and icon source stored inside a .lnk. Null for links with no file target (Store apps, shell: paths).</summary>
    public static (string Target, string Args, string IconSource)? ReadLink(string linkPath)
    {
        object? instance = null;
        try
        {
            instance = new ShellLink();
            if (((IPersistFile)instance).Load(linkPath, StgmRead) != 0) return null;

            var link = (IShellLinkW)instance;

            var target = new StringBuilder(1024);
            if (link.GetPath(target, target.Capacity, IntPtr.Zero, 0) != 0) return null;
            var path = target.ToString();
            if (string.IsNullOrWhiteSpace(path)) return null;

            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);

            var iconPath = new StringBuilder(1024);
            link.GetIconLocation(iconPath, iconPath.Capacity, out _);
            var icon = iconPath.ToString();
            var comma = icon.LastIndexOf(',');
            if (comma > 1) icon = icon[..comma];
            if (!File.Exists(icon)) icon = "";

            return (path, args.ToString().Trim(), icon);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("read link " + linkPath + ": " + ex.Message);
            return null;
        }
        finally
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>The URL= line of an internet shortcut, or null when the file has none.</summary>
    public static string? ReadUrl(string urlFilePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlFilePath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;

                var url = trimmed[4..].Trim();
                return url.Length > 0 ? url : null;
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("read url " + urlFilePath + ": " + ex.Message);
        }

        return null;
    }

    private const uint StgmRead = 0;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Only the slots up to GetArguments are ever called; the earlier ones keep the vtable order right.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription(string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory(string pszDir);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments(string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }
}
