using System.IO;
using Button = Wpf.Ui.Controls.Button;
using System.Windows;
using System.Windows.Controls;
using Meili.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Meili.App.Views.Controls;

/// <summary>Trusted, bundled chart surface. Untrusted news content uses a separate profile and window.</summary>
public sealed class WebChartControl : UserControl
{
    private WebView2? _web;
    private readonly DockPanel _root = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _install = new() { Content = LocalizationService.Current["Reader_Install"], Visibility = Visibility.Collapsed };
    public WebChartSession? Session { get; private set; }
    public string? LastDiagnostic { get; private set; }
    public WebChartControl()
    {
        _status.FontSize = 12;
        _status.Margin = new Thickness(12, 8, 12, 8);
        _status.Padding = new Thickness(12, 8, 12, 8);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _status.SetResourceReference(TextBlock.BackgroundProperty, "InfoBarBrush");
        _install.FontSize = 14; _install.MinHeight = 32;
        _install.Padding = new Thickness(12, 6, 12, 6); _install.Margin = new Thickness(12, 0, 12, 8);
        _install.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        DockPanel.SetDock(_status, Dock.Top); _root.Children.Add(_status);
        DockPanel.SetDock(_install, Dock.Top); _root.Children.Add(_install);
        _install.Click += (_, _) => Ui.Platform.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/#download-section");
        Content = _root;
    }
    public void Start(ChartsViewModel vm, ChartPalette palette) => Start(() => new WebChartSession(vm), palette);
    public void Start(StrategyViewModel vm, ChartPalette palette) => Start(() => new WebChartSession(vm), palette);
    private void Start(Func<WebChartSession> create, ChartPalette palette)
    {
        Stop();
        Session = create(); Session.Update(palette);
        Session.DiagnosticReceived += OnDiagnostic;
        _status.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.BackgroundProperty, "InfoBarBrush");
        _status.Text = LocalizationService.Current[Session?.IsStrategyWorkbench == true ? "Wb_Loading" : "Ch_WebLoading"];
        var web = new WebView2(); _web = web;
        web.Loaded += async (_, _) => await InitializeAsync(web, Session);
        _root.Children.Add(web);
    }
    private void OnDiagnostic(System.Text.Json.JsonElement e)
    {
        LastDiagnostic = e.GetRawText();
        _status.Visibility = e.GetProperty("status").GetString() == "rendered" ? Visibility.Collapsed : Visibility.Visible;
        if (e.GetProperty("status").GetString() == "rendered") _status.Text = ""; else ShowFailure();
        if (e.TryGetProperty("message", out var message)) AppLog.Warn("web-chart", message.GetString() ?? "Chart script failed");
    }
    private void ShowFailure()
    {
        _status.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.BackgroundProperty, "ErrorBarBrush");
        _status.Text = LocalizationService.Current[Session?.IsStrategyWorkbench == true ? "Wb_LoadFailed" : "Ch_WebFailed"];
    }
    private async Task InitializeAsync(WebView2 web, WebChartSession? session)
    {
        if (session is null) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppServices.DataDirectory.Path, "browser", "charts"));
            if (_web != web || Session != session) return;
            await web.EnsureCoreWebView2Async(env);
            if (_web != web || Session != session) return;
            var core = web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) => e.Cancel = !session.IsPageUrl(e.Uri);
            core.ProcessFailed += (_, _) => ShowFailure();
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) ShowFailure(); };
            core.Navigate(session.PageUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            AppLog.Warn("web-chart", "WebView2 chart initialization failed", ex);
            if (_web != web) return;
            ShowFailure();
            _status.Text = LocalizationService.Current[ex is WebView2RuntimeNotFoundException ? "Reader_Runtime" : "Ch_WebFailed"];
            _install.Visibility = ex is WebView2RuntimeNotFoundException ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    public void Update(ChartPalette palette) => Session?.Update(palette);
    public void Stop()
    {
        if (Session is { } session) { session.DiagnosticReceived -= OnDiagnostic; session.Dispose(); Session = null; }
        if (_web is { } web) { _web = null; _root.Children.Remove(web); web.Dispose(); }
        LastDiagnostic = null; _install.Visibility = Visibility.Collapsed;
    }
}
