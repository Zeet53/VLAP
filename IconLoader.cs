using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VLAP;

public static class IconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? _folderIcon;
    private static bool _folderTried;

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const int ShilJumbo = 0x4;
    private const int IldTransparent = 0x1;

    private static readonly Guid IidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // only the first eight members of IImageList are declared; GetIcon must keep its vtable slot
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList? ppv);

    /// <summary>The stock Windows folder icon. Call it on the UI thread once — the result is cached and frozen.</summary>
    public static ImageSource? Folder()
    {
        if (_folderTried) return _folderIcon;
        _folderTried = true;

        try
        {
            var info = new ShFileInfo();
            var flags = ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes;
            if (SHGetFileInfo("folder", FileAttributeDirectory, ref info,
                    (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero ||
                info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                // jumbo frame first: 32px turns to mush once the UI is scaled up
                if (TryJumboIcon(info.iIcon, out var jumbo))
                {
                    try { _folderIcon = FromHIcon(jumbo); }
                    finally { DestroyIcon(jumbo); }
                }

                _folderIcon ??= FromHIcon(info.hIcon);
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("folder icon: " + ex.Message);
        }

        return _folderIcon;
    }

    private static bool TryJumboIcon(int index, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        IImageList? list = null;

        try
        {
            var iid = IidImageList;
            if (SHGetImageList(ShilJumbo, ref iid, out list) != 0 || list == null) return false;
            return list.GetIcon(index, IldTransparent, ref icon) == 0 && icon != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
        }
    }

    private static ImageSource FromHIcon(IntPtr handle)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] icons, uint[]? ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            if (!File.Exists(path)) return null;

            // ask for a big frame first — 32px icons turn to mush once the UI is scaled
            int[] sizes = { 256, 96, 64, 48 };
            foreach (var size in sizes)
            {
                var handles = new IntPtr[1];
                if (PrivateExtractIcons(path, 0, size, size, handles, null, 1, 0) == 0 ||
                    handles[0] == IntPtr.Zero) continue;

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    result = source;
                    break;
                }
                finally { DestroyIcon(handles[0]); }
            }

            if (result == null)
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    result = source;
                }
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("icon " + path + ": " + ex.Message);
        }

        Cache[path] = result;
        return result;
    }

    public static void ClearCache() => Cache.Clear();
}
