using System.Runtime.InteropServices;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;

namespace Meili.Mac.Platform;

/// <summary>AppKit's variable-width NSStatusItem. Only create/dispose on the macOS UI thread.</summary>
public sealed class MacQuoteStatusItem : IQuoteStatusItem
{
    private IntPtr _status, _menu, _target;
    private readonly Action? _openPanel;
    private IReadOnlyList<QuoteMenuEntry> _entries = [];
    private readonly Dictionary<long, Action> _activeActions = [];
    public MacQuoteStatusItem(Action? openPanel=null)
    {
        _openPanel=openPanel;
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        try
        {
            Native.EnsureClass();
            _target = Native.Send(Native.TargetClass, "new"); Native.Owners.Add(_target, this);
            _menu = Native.Send(Native.Class("NSMenu"), "new");
            Native.Bool(_menu, "setAutoenablesItems:", false); Native.Send(_menu, "setDelegate:", _target);
            _status = Native.Length(Native.Send(Native.Class("NSStatusBar"), "systemStatusBar"), Native.Sel("statusItemWithLength:"), -1);
            if (_status == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("Could not create NSStatusItem"); }
            Native.Send(_status, "retain");
            if(openPanel is null)Native.Send(_status,"setMenu:",_menu);
            else{var button=Native.Send(_status,"button");Native.Send(button,"setTarget:",_target);Native.Send(button,"setAction:",Native.Sel("openPanel:"));}
        }
        catch { Dispose(); throw; }
    }
    public (double X,double Y) Anchor
    {
        get{var frame=Native.Frame(Native.Send(Native.Send(_status,"button"),"window"));var main=Native.Frame(Native.Send(Native.Send(Native.Class("NSScreen"),"screens"),"objectAtIndex:",IntPtr.Zero));return(frame.X+frame.Width/2,main.Height-frame.Y);}
    }
    public void InvokePanel()=>Native.Send(Native.Send(_status,"button"),"performClick:",IntPtr.Zero);
    public string Title => Native.Text(Native.Send(Native.Send(_status, "button"), "title"));
    public void Update(MenuBarQuote quote, IReadOnlyList<QuoteMenuEntry> entries, UpDownColorMode colors)
    {
        if (_status == IntPtr.Zero) return; _entries = entries;
        var button = Native.Send(_status, "button");
        Native.SetText(button, "setTitle:", quote.Title); Native.SetText(button, "setToolTip:", quote.Tooltip);
        Native.SetText(button, "setAccessibilityLabel:", quote.Tooltip);
        var color = Native.Send(Native.Class("NSColor"), quote.Direction == 0 ? "labelColor" : (quote.Direction > 0) == (colors == UpDownColorMode.RedUp) ? "systemRedColor" : "systemGreenColor");
        var attributes = Native.Send(Native.Class("NSMutableDictionary"), "new");
        var key = Native.String("NSColor"); var text = Native.String(quote.Title);
        try
        {
            Native.SetAttribute(attributes, Native.Sel("setObject:forKey:"), color, key);
            var value = Native.InitAttributed(Native.Send(Native.Class("NSAttributedString"), "alloc"), Native.Sel("initWithString:attributes:"), text, attributes);
            Native.Send(button, "setAttributedTitle:", value); Native.Send(value, "release");
        }
        finally { Native.Send(key, "release"); Native.Send(text, "release"); Native.Send(attributes, "release"); }
    }
    public void PrepareMenu()
    {
        Native.Send(_menu, "removeAllItems"); _activeActions.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Separator) { Native.Send(_menu, "addItem:", Native.Send(Native.Class("NSMenuItem"), "separatorItem")); continue; }
            var item = Native.Send(Native.Class("NSMenuItem"), "new");
            try
            {
                Native.SetText(item, "setTitle:", entry.Title);
                Native.Bool(item, "setEnabled:", entry.Action is not null);
                Native.Integer(item, Native.Sel("setState:"), entry.Checked ? 1 : 0);
                if (entry.Action is { } action)
                {
                    _activeActions.Add(i, action); Native.Integer(item, Native.Sel("setTag:"), i);
                    Native.Send(item, "setTarget:", _target); Native.Send(item, "setAction:", Native.Sel("activate:"));
                }
                Native.Send(_menu, "addItem:", item);
            }
            finally { Native.Send(item, "release"); }
        }
    }
    /// <summary>Exercises AppKit target/action without a blocking menu-tracking loop; used by native tests.</summary>
    public void InvokeMenuItem(int index) => Native.Integer(_menu, Native.Sel("performActionForItemAtIndex:"), index);
    public void CaptureButton(string path)
    {
        var button = Native.Send(_status, "button"); var bounds = Native.Bounds(button);
        var bitmap = Native.Bitmap(button, Native.Sel("bitmapImageRepForCachingDisplayInRect:"), bounds);
        if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Status button capture failed");
        Native.Cache(button, Native.Sel("cacheDisplayInRect:toBitmapImageRep:"), bounds, bitmap);
        var data = Native.Png(bitmap, Native.Sel("representationUsingType:properties:"), 4, Native.Send(Native.Class("NSDictionary"), "dictionary"));
        var file = Native.String(Path.GetFullPath(path));
        try { if (!Native.Write(data, Native.Sel("writeToFile:atomically:"), file, true)) throw new IOException("Status button PNG write failed"); }
        finally { Native.Send(file, "release"); }
    }
    public void Dispose()
    {
        if (_status != IntPtr.Zero) { Native.Send(_status, "setMenu:", IntPtr.Zero); Native.Send(Native.Send(Native.Class("NSStatusBar"), "systemStatusBar"), "removeStatusItem:", _status); Native.Send(_status, "release"); _status = IntPtr.Zero; }
        if (_menu != IntPtr.Zero) { Native.Send(_menu, "setDelegate:", IntPtr.Zero); Native.Send(_menu, "release"); _menu = IntPtr.Zero; }
        if (_target != IntPtr.Zero) { Native.Owners.Remove(_target); Native.Send(_target, "release"); _target = IntPtr.Zero; }
        _activeActions.Clear();
    }
    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly Dictionary<IntPtr, MacQuoteStatusItem> Owners = [];
        internal static IntPtr TargetClass;
        [DllImport(ObjC, EntryPoint = "objc_getClass")] internal static extern IntPtr Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] internal static extern IntPtr Sel(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr parent, string name, nuint size);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)][return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr callback, string signature);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr Length(IntPtr obj, IntPtr sel, double length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void Integer(IntPtr obj, IntPtr sel, long value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Boolean(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SetAttribute(IntPtr obj, IntPtr sel, IntPtr value, IntPtr key);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr InitAttributed(IntPtr obj, IntPtr sel, IntPtr text, IntPtr attributes);
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(double X, double Y, double Width, double Height);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Rect RectMessage(IntPtr obj, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void RectMessageX64(out Rect result, IntPtr obj, IntPtr sel);
        internal static Rect Frame(IntPtr obj) { if (RuntimeInformation.ProcessArchitecture == Architecture.X64) { RectMessageX64(out var rect, obj, Sel("frame")); return rect; } return RectMessage(obj, Sel("frame")); }
        internal static Rect Bounds(IntPtr obj) { if (RuntimeInformation.ProcessArchitecture == Architecture.X64) { RectMessageX64(out var rect, obj, Sel("bounds")); return rect; } return RectMessage(obj, Sel("bounds")); }
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr Bitmap(IntPtr obj, IntPtr sel, Rect bounds);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void Cache(IntPtr obj, IntPtr sel, Rect bounds, IntPtr bitmap);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr Png(IntPtr obj, IntPtr sel, long type, IntPtr properties);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.I1)] internal static extern bool Write(IntPtr obj, IntPtr sel, IntPtr file, [MarshalAs(UnmanagedType.I1)] bool atomic);
        internal static IntPtr Send(IntPtr obj, string selector) => Message(obj, Sel(selector));
        internal static IntPtr Send(IntPtr obj, string selector, IntPtr arg) => Message(obj, Sel(selector), arg);
        internal static void Bool(IntPtr obj, string selector, bool value) => Boolean(obj, Sel(selector), value);
        internal static string Text(IntPtr text) => Marshal.PtrToStringUTF8(Send(text, "UTF8String")) ?? "";
        internal static IntPtr String(string text) { var utf8 = Marshal.StringToCoTaskMemUTF8(text); try { return Send(Send(Class("NSString"), "alloc"), "initWithUTF8String:", utf8); } finally { Marshal.FreeCoTaskMem(utf8); } }
        internal static void SetText(IntPtr obj, string selector, string text) { var value = String(text); try { Send(obj, selector, value); } finally { Send(value, "release"); } }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(IntPtr self, IntPtr cmd, IntPtr arg);
        private static readonly Callback ActivateRoot = (self, _, item) => Guard(self, owner => { if (owner._activeActions.TryGetValue(Send(item, "tag").ToInt64(), out var action)) action(); });
        private static readonly Callback PanelRoot = (self,_,_)=>Guard(self,owner=>owner._openPanel?.Invoke());
        private static readonly Callback MenuRoot = (self, _, _) => Guard(self, owner => owner.PrepareMenu());
        private static void Guard(IntPtr self, Action<MacQuoteStatusItem> action) { try { if (Owners.TryGetValue(self, out var owner)) action(owner); } catch (Exception ex) { AppLog.Error("menu-bar", "Native menu callback failed", ex); } }
        internal static void EnsureClass()
        {
            if (TargetClass != IntPtr.Zero) return;
            TargetClass = objc_allocateClassPair(Class("NSObject"), "EasySellPutQuoteStatusActions", 0);
            if (TargetClass == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate status menu target");
            if (!class_addMethod(TargetClass,Sel("openPanel:"),Marshal.GetFunctionPointerForDelegate(PanelRoot),"v@:@") || !class_addMethod(TargetClass, Sel("activate:"), Marshal.GetFunctionPointerForDelegate(ActivateRoot), "v@:@") || !class_addMethod(TargetClass, Sel("menuNeedsUpdate:"), Marshal.GetFunctionPointerForDelegate(MenuRoot), "v@:@")) throw new InvalidOperationException("Cannot register status menu actions");
            objc_registerClassPair(TargetClass);
        }
    }
}
