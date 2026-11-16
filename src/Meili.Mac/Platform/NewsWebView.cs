using Avalonia;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Meili.Core.Diagnostics;

namespace Meili.Mac.Platform;

/// <summary>System WKWebView hosted directly as an NSView; no redistributed browser engine or JavaScript bridge.</summary>
public sealed class NewsWebView : NativeControlHost
{
    private IntPtr _view, _delegate;
    private string? _pending;
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _lastBack, _lastForward;
    private bool _presentationActive=true;private Window? _owner;
    internal bool IsPresentationPolling=>_stateTimer.IsEnabled;

    public NewsWebView()
    {
        // WebKit title/history can change after didFinishNavigation (including client-rendered headlines).
        // Poll only while the native view exists; no KVO observer lifetime or extra native library is needed.
        AttachedToVisualTree+=(_,_)=>{_owner=TopLevel.GetTopLevel(this) as Window;if(_owner is not null)_owner.PropertyChanged+=OnOwnerChanged;SyncPresentation();};
        DetachedFromVisualTree+=(_,_)=>{if(_owner is not null)_owner.PropertyChanged-=OnOwnerChanged;_owner=null;_stateTimer.Stop();};
        _stateTimer.Tick += (_, _) => { if (_view != IntPtr.Zero) Update(StatusKey); };
    }
    private void OnOwnerChanged(object? sender,AvaloniaPropertyChangedEventArgs e){if(e.Property==Window.IsVisibleProperty||e.Property==Window.WindowStateProperty)SyncPresentation();}
    public void SetPresentationActive(bool active){_presentationActive=active;SyncPresentation();}
    private void SyncPresentation()
    {
        var active=_presentationActive&&IsEffectivelyVisible&&(_owner is null||_owner.IsVisible&&_owner.WindowState!=WindowState.Minimized);
        if(active&&_view!=IntPtr.Zero){if(!_stateTimer.IsEnabled)_stateTimer.Start();}else _stateTimer.Stop();
        if(_view!=IntPtr.Zero)Native.Send(_view,"setHidden:",active?IntPtr.Zero:new IntPtr(1));
    }
    // Only the bundled chart host supplies a stricter same-document navigation policy.
    internal Func<string, bool>? NavigationPolicy { get; init; }
    private bool IsAllowed(string url) => NavigationPolicy?.Invoke(url) ?? NewsReaderPolicy.IsAllowed(url);
    public event Action? Changed;
    public string Address { get; private set; } = "";
    public string DocumentTitle { get; private set; } = "";
    public string StatusKey { get; private set; } = "";
    public bool CanGoBack => _view != IntPtr.Zero && Native.Bool(_view, "canGoBack");
    public bool CanGoForward => _view != IntPtr.Zero && Native.Bool(_view, "canGoForward");

    public void Navigate(string url)
    {
        if (!IsAllowed(url)) { StatusKey = "Reader_Blocked"; Changed?.Invoke(); return; }
        _pending = url; Address = url;
        if (_view == IntPtr.Zero) return;
        var text = Native.String(url);
        try
        {
            var nsurl = Native.Send(Native.Class("NSURL"), "URLWithString:", text);
            var request = Native.Send(Native.Class("NSURLRequest"), "requestWithURL:", nsurl);
            Native.Send(_view, "loadRequest:", request);
        }
        finally { Native.Send(text, "release"); }
    }
    public void Back() { if (CanGoBack) Native.Send(_view, "goBack"); }
    public void Forward() { if (CanGoForward) Native.Send(_view, "goForward"); }
    public void Reload() { if (_view != IntPtr.Zero) Native.Send(_view, "reload"); }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            Native.EnsureLoaded();
            var config = Native.Send(Native.Class("WKWebViewConfiguration"), "new");
            Native.Send(config, "setWebsiteDataStore:", Native.Send(Native.Class("WKWebsiteDataStore"), "nonPersistentDataStore"));
            try { _view = Native.Init(Native.Send(Native.Class("WKWebView"), "alloc"), Native.Selector("initWithFrame:configuration:"), new Native.Rect(0, 0, 1, 1), config); }
            finally { Native.Send(config, "release"); }
            if (_view == IntPtr.Zero) throw new InvalidOperationException("WKWebView allocation failed");
            _delegate = Native.Send(Native.DelegateClass, "new");
            if (_delegate == IntPtr.Zero) throw new InvalidOperationException("WKWebView delegate allocation failed");
            Native.Owners[_delegate] = this;
            AppLog.Info("news-reader", "WKWebView initialized");
            Native.Send(_view, "setNavigationDelegate:", _delegate);
            Native.Send(_view, "setUIDelegate:", _delegate);
            SyncPresentation();
            if (_pending is not null) Navigate(_pending);
            return new PlatformHandle(_view, "NSView");
        }
        catch (Exception ex)
        {
            ReleaseNative();
            AppLog.Warn("news-reader", "WKWebView initialization failed", ex);
            StatusKey = "Reader_Failed"; Changed?.Invoke();
            return base.CreateNativeControlCore(parent);
        }
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_view == IntPtr.Zero) { base.DestroyNativeControlCore(control); return; }
        ReleaseNative();
    }
    private void ReleaseNative()
    {
        _stateTimer.Stop();
        if (_view == IntPtr.Zero) return;
        AppLog.Info("news-reader", "WKWebView disposed");
        Native.Send(_view, "stopLoading");
        Native.Send(_view, "setNavigationDelegate:", IntPtr.Zero);
        Native.Send(_view, "setUIDelegate:", IntPtr.Zero);
        Native.Owners.TryRemove(_delegate, out _);
        Native.Send(_delegate, "release"); _delegate = IntPtr.Zero;
        Native.Send(_view, "removeFromSuperview");
        Native.Send(_view, "release"); _view = IntPtr.Zero;
    }
    private void Update(string status)
    {
        var address = Native.Text(Native.Send(Native.Send(_view, "URL"), "absoluteString"));
        var title = Native.Text(Native.Send(_view, "title"));
        var back = CanGoBack; var forward = CanGoForward;
        var changed = status != StatusKey || address != Address || title != DocumentTitle || back != _lastBack || forward != _lastForward;
        StatusKey = status; Address = address; DocumentTitle = title; _lastBack = back; _lastForward = forward;
        if (changed) Changed?.Invoke();
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly ConcurrentDictionary<IntPtr, NewsWebView> Owners = new();
        internal static IntPtr DelegateClass;
        private static IntPtr _framework;
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(double X, double Y, double Width, double Height);
        [DllImport(ObjC, EntryPoint = "objc_getClass")] internal static extern IntPtr Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] internal static extern IntPtr Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr Init(IntPtr obj, IntPtr selector, Rect frame, IntPtr config);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
        internal static IntPtr Send(IntPtr obj, string selector) => obj == IntPtr.Zero ? IntPtr.Zero : Message(obj, Selector(selector));
        internal static IntPtr Send(IntPtr obj, string selector, IntPtr arg) => Message(obj, Selector(selector), arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte BoolMessage(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte BoolMessage(IntPtr obj, IntPtr selector, IntPtr arg);
        internal static bool Bool(IntPtr obj, string selector) => BoolMessage(obj, Selector(selector)) != 0;
        internal static string Text(IntPtr str) => str == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(str, "UTF8String")) ?? "";
        internal static IntPtr String(string value)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try { return Send(Send(Class("NSString"), "alloc"), "initWithUTF8String:", utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Policy(IntPtr self, IntPtr cmd, IntPtr view, IntPtr action, IntPtr block);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Decide(IntPtr block, nint policy);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Finished(IntPtr self, IntPtr cmd, IntPtr view, IntPtr navigation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Failed(IntPtr self, IntPtr cmd, IntPtr view, IntPtr navigation, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Popup(IntPtr self, IntPtr cmd, IntPtr view, IntPtr config, IntPtr action, IntPtr features);
        private static readonly Policy PolicyRoot = Navigation;
        private static readonly Policy ResponseRoot = Response;
        private static readonly Finished FinishedRoot = Finish;
        private static readonly Failed FailedRoot = Fail;
        private static readonly Popup PopupRoot = NewWindow;
        internal static void EnsureLoaded()
        {
            if (_framework != IntPtr.Zero) return;
            _framework = NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");
            DelegateClass = objc_allocateClassPair(Class("NSObject"), "MeiliNewsNavigationDelegate", 0);
            Add("webView:decidePolicyForNavigationAction:decisionHandler:", PolicyRoot, "v@:@@@?");
            Add("webView:decidePolicyForNavigationResponse:decisionHandler:", ResponseRoot, "v@:@@@?");
            Add("webView:didFinishNavigation:", FinishedRoot, "v@:@@");
            Add("webView:didStartProvisionalNavigation:", StartRoot, "v@:@@");
            Add("webView:didFailNavigation:withError:", FailedRoot, "v@:@@@");
            Add("webView:didFailProvisionalNavigation:withError:", FailedRoot, "v@:@@@");
            Add("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", PopupRoot, "@@:@@@@");
            objc_registerClassPair(DelegateClass);
        }
        private static void Add(string name, Delegate callback, string types)
        {
            if (!class_addMethod(DelegateClass, Selector(name), Marshal.GetFunctionPointerForDelegate(callback), types))
                throw new InvalidOperationException("Could not register WebKit delegate " + name);
        }
        private static void Complete(IntPtr block, bool allow) =>
            Marshal.GetDelegateForFunctionPointer<Decide>(Marshal.ReadIntPtr(block, 2 * IntPtr.Size))(block, allow ? 1 : 0);
        private static void Navigation(IntPtr self, IntPtr cmd, IntPtr view, IntPtr action, IntPtr block)
        {
            var allow = false;
            try
            {
                if (!Owners.TryGetValue(self, out var owner)) return;
                var url = Text(Send(Send(Send(action, "request"), "URL"), "absoluteString"));
                allow = owner.IsAllowed(url);
                AppLog.Info("news-reader", "Navigation policy: " + (allow ? "allow" : "block"));
                if (!allow) { owner.StatusKey = "Reader_Blocked"; owner.Changed?.Invoke(); }
                else if (Send(action, "targetFrame") == IntPtr.Zero) { allow = false; owner.Navigate(url); }
            }
            catch (Exception ex) { AppLog.Warn("news-reader", "WebKit navigation failed", ex); }
            finally { Complete(block, allow); }
        }
        private static void Response(IntPtr self, IntPtr cmd, IntPtr view, IntPtr response, IntPtr block)
        {
            var allow = true;
            try
            {
                if (!Bool(response, "canShowMIMEType"))
                {
                    allow = false;
                    if (Owners.TryGetValue(self, out var blocked)) blocked.Update("Reader_Blocked");
                    return;
                }
                AppLog.Info("news-reader", "Navigation response received");
                var http = Send(response, "response");
                if (BoolMessage(http, Selector("isKindOfClass:"), Class("NSHTTPURLResponse")) != 0 && Send(http, "statusCode").ToInt64() >= 400)
                { allow = false; if (Owners.TryGetValue(self, out var owner)) owner.Update("Reader_Failed"); }
            }
            catch (Exception ex) { allow = false; AppLog.Warn("news-reader", "WebKit response failed", ex); }
            finally { Complete(block, allow); }
        }
        private static readonly Finished StartRoot = (self, _, _, _) => Notify(self, "Reader_Loading");
        private static void Finish(IntPtr self, IntPtr cmd, IntPtr view, IntPtr navigation)
        { AppLog.Info("news-reader", "Navigation finished"); Notify(self, ""); }
        private static void Fail(IntPtr self, IntPtr cmd, IntPtr view, IntPtr navigation, IntPtr error)
        {
            var code = Send(error, "code").ToInt64();
            if (code == -999) return;
            AppLog.Warn("news-reader", "WebKit load failed: " + Text(Send(error, "domain")) + " " + code);
            Notify(self, "Reader_Failed");
        }
        private static void Notify(IntPtr self, string status)
        {
            try { if (Owners.TryGetValue(self, out var owner)) owner.Update(status); }
            catch (Exception ex) { AppLog.Warn("news-reader", "WebKit state callback failed", ex); }
        }
        private static IntPtr NewWindow(IntPtr self, IntPtr cmd, IntPtr view, IntPtr config, IntPtr action, IntPtr features)
        {
            try { if (Owners.TryGetValue(self, out var owner)) owner.Navigate(Text(Send(Send(Send(action, "request"), "URL"), "absoluteString"))); }
            catch (Exception ex) { AppLog.Warn("news-reader", "WebKit popup failed", ex); }
            return IntPtr.Zero;
        }
    }
}
