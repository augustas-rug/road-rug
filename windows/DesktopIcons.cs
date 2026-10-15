using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RoadRug;

/// Reads and sets where Explorer has put the desktop icons, through the desktop's IFolderView.
/// Positions are icon centres in screen pixels.
static class DesktopIcons
{
    public readonly record struct Icon(string Path, Point Center);

    static IFolderView view;
    static IShellFolder folder;

    /// Every icon on the desktop with its centre, or an empty list if Explorer isn't reachable.
    public static List<Icon> List()
    {
        var result = new List<Icon>();
        if (!Connect()) return result;
        try
        {
            var spacing = new POINT();
            view.GetSpacing(ref spacing);
            var origin = SystemInformation.VirtualScreen.Location;
            if (view.ItemCount(SVGIO_ALLVIEW, out int count) != 0) { Reset(); return result; }
            for (int i = 0; i < count; i++)
            {
                if (view.Item(i, out var pidl) != 0) continue;
                try
                {
                    var path = PathOf(pidl);
                    if (path == null || view.GetItemPosition(pidl, out var pt) != 0) continue;
                    result.Add(new Icon(path, new Point(origin.X + pt.X + spacing.X / 2, origin.Y + pt.Y + (int)(spacing.Y * 0.35f))));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (COMException) { Reset(); }
        catch (InvalidCastException) { Reset(); }
        return result;
    }

    /// Moves a desktop icon so its centre is at `center` (screen pixels). False if it isn't on the desktop (yet).
    public static bool Place(string path, Point center)
    {
        if (!Connect()) return false;
        try
        {
            var spacing = new POINT();
            view.GetSpacing(ref spacing);
            var origin = SystemInformation.VirtualScreen.Location;
            if (view.ItemCount(SVGIO_ALLVIEW, out int count) != 0) { Reset(); return false; }
            for (int i = 0; i < count; i++)
            {
                if (view.Item(i, out var pidl) != 0) continue;
                try
                {
                    if (!string.Equals(PathOf(pidl), path, StringComparison.OrdinalIgnoreCase)) continue;
                    var pt = new POINT { X = center.X - origin.X - spacing.X / 2, Y = center.Y - origin.Y - (int)(spacing.Y * 0.35f) };
                    return view.SelectAndPositionItems(1, new[] { pidl }, new[] { pt }, SVSI_POSITIONITEM) == 0;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (COMException) { Reset(); }
        catch (InvalidCastException) { Reset(); }
        return false;
    }

    static string PathOf(IntPtr pidl)
    {
        var strret = Marshal.AllocCoTaskMem(1024);
        try
        {
            if (folder.GetDisplayNameOf(pidl, SHGDN_FORPARSING, strret) != 0) return null;
            var sb = new StringBuilder(1024);
            return StrRetToBuf(strret, pidl, sb, (uint)sb.Capacity) == 0 ? sb.ToString() : null;
        }
        finally { Marshal.FreeCoTaskMem(strret); }
    }

    static void Reset() { view = null; folder = null; }

    /// Finds the desktop's folder view: ShellWindows -> desktop browser -> active shell view -> IFolderView.
    static bool Connect()
    {
        if (view != null) return true;
        try
        {
            var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));
            object loc = CSIDL_DESKTOP, root = null;
            if (windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH, out var disp) != 0 || disp == null) return false;
            var sid = SID_STopLevelBrowser; var iid = typeof(IShellBrowser).GUID;
            if (((IServiceProvider)disp).QueryService(ref sid, ref iid, out var browserObj) != 0) return false;
            if (((IShellBrowser)browserObj).QueryActiveShellView(out var shellView) != 0) return false;
            var fv = (IFolderView)shellView;
            var folderIid = typeof(IShellFolder).GUID;
            if (fv.GetFolder(ref folderIid, out var folderObj) != 0) return false;
            folder = (IShellFolder)folderObj;
            view = fv;
            return true;
        }
        catch (Exception) { Reset(); return false; }
    }

    // MARK: COM plumbing

    const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    const uint SVGIO_ALLVIEW = 2, SVSI_POSITIONITEM = 0x80, SHGDN_FORPARSING = 0x8000;
    static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrRetToBuf(IntPtr pstr, IntPtr pidl, StringBuilder buf, uint cch);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
        [PreserveSig] int Register(IntPtr pid, int hwnd, int swClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, IntPtr loc, IntPtr locRoot, int swClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, IntPtr loc);
        [PreserveSig] int OnActivated(int cookie, short active);
        [PreserveSig] int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object loc, [MarshalAs(UnmanagedType.Struct)] ref object locRoot,
                                       int swClass, out int hwnd, int options, [MarshalAs(UnmanagedType.IDispatch)] out object disp);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr shared, IntPtr active);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr msg, short id);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr ret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object shellView);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT pt);
        [PreserveSig] int GetSpacing(ref POINT pt);
        [PreserveSig] int GetDefaultSpacing(out POINT pt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int item, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
                                                 [MarshalAs(UnmanagedType.LPArray)] POINT[] points, uint flags);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lparam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr pidls, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, IntPtr pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
    }
}
