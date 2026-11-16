using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Meili.Core.Diagnostics;

namespace Meili.Mac.Platform;

/// <summary>Local chart document in system WKWebView, without exposing a native JavaScript bridge.</summary>
public sealed class WebChartControl : UserControl
{
    private readonly DockPanel _root = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8) };
    private NewsWebView? _web;
    public WebChartSession? Session { get; private set; }
    public string? LastDiagnostic { get; private set; }
    public WebChartControl()
    {
        _status.FontSize = 12;
        _status.Margin = new Thickness(12, 8, 12, 8);
        _status.Padding = new Thickness(12, 8);
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
        _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("InfoBarBrush"));
        DockPanel.SetDock(_status, Dock.Top); _root.Children.Add(_status); Content = _root;
    }
    public void Start(ChartsViewModel vm, ChartPalette palette) => Start(() => new WebChartSession(vm), palette);
    public void Start(StrategyViewModel vm, ChartPalette palette) => Start(() => new WebChartSession(vm), palette);
    private void Start(Func<WebChartSession> create, ChartPalette palette)
    {
        Stop();
        var session = create(); Session = session; session.Update(palette);
        session.DiagnosticReceived += OnDiagnostic;
        _status.IsVisible = true;
        _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("InfoBarBrush"));
        _status.Text = LocalizationService.Current[Session?.IsStrategyWorkbench == true ? "Wb_Loading" : "Ch_WebLoading"];
        var web = new NewsWebView { NavigationPolicy = session.IsPageUrl }; _web = web;
        web.Changed += OnNativeChanged;
        _root.Children.Add(web); web.Navigate(session.PageUri.AbsoluteUri);
    }
    private void OnNativeChanged()
    {
        if (_web?.StatusKey == "Reader_Failed") { _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("ErrorBarBrush")); _status.IsVisible = true; _status.Text = LocalizationService.Current[Session?.IsStrategyWorkbench == true ? "Wb_LoadFailed" : "Ch_WebFailed"]; }
    }
    private void OnDiagnostic(System.Text.Json.JsonElement e)
    {
        LastDiagnostic = e.GetRawText();
        _status.IsVisible = e.GetProperty("status").GetString() != "rendered";
        _status.Text = e.GetProperty("status").GetString() == "rendered" ? "" : LocalizationService.Current[Session?.IsStrategyWorkbench == true ? "Wb_LoadFailed" : "Ch_WebFailed"];
        _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("ErrorBarBrush"));
        if (e.TryGetProperty("message", out var message)) AppLog.Warn("web-chart", message.GetString() ?? "Chart script failed");
    }
    public void Update(ChartPalette palette) => Session?.Update(palette);
    public void Stop()
    {
        if (Session is { } session) { session.DiagnosticReceived -= OnDiagnostic; session.Dispose(); Session = null; }
        if (_web is { } web) { web.Changed -= OnNativeChanged; _root.Children.Remove(web); _web = null; }
        LastDiagnostic = null;
    }
}
