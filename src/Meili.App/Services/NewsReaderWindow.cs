using System.IO;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Meili.Core.Diagnostics;

namespace Meili.App.Services;

/// <summary>Lazily initialized Evergreen engine: the browser runtime is never bundled with the app.</summary>
public sealed class NewsReaderWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _address = new() { IsReadOnly = true, ClearButtonEnabled = false, MinWidth = 180 };
    private readonly Button _back = new(), _forward = new(), _reload = new(), _external = new(), _install = new();
    private readonly LocalizationService _loc = LocalizationService.Current;
    private string? _pending;
    private bool _ready, _closed;

    public NewsReaderWindow()
    {
        SetResourceReference(FontFamilyProperty,"AppFont");
        Width = 1050; Height = 760; MinWidth = 640; MinHeight = 400;
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(FontFamilyProperty, "AppFont");
        _back.IsEnabled = _forward.IsEnabled = false;
        _address.Margin = new Thickness(12, 0, 12, 8);
        _address.Padding = new Thickness(10, 6, 10, 6);
        _address.SetResourceReference(TextBox.BackgroundProperty, "ControlFillColorSecondaryBrush");
        _address.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
        _address.SetResourceReference(TextBox.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        _status.Margin = new Thickness(12, 0, 12, 8);
        _status.Padding = new Thickness(12, 8, 12, 8);
        _status.FontSize = 12;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _status.SetResourceReference(TextBlock.BackgroundProperty, "InfoBarBrush");
        _status.Visibility = Visibility.Collapsed;
        var root = new DockPanel();
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 6, 2) };
        foreach (var button in new[] { _back, _forward, _reload, _external, _install })
        {
            button.Margin = new Thickness(0, 0, 6, 6); button.Padding = new Thickness(12, 6, 12, 6);
            button.FontSize = 14; button.MinHeight = 32;
            button.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_address, Dock.Top); root.Children.Add(_address);
        DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        root.Children.Add(_web); Content = root;
        _back.Click += (_, _) => { if (_ready && _web.CanGoBack) _web.GoBack(); };
        _forward.Click += (_, _) => { if (_ready && _web.CanGoForward) _web.GoForward(); };
        _reload.Click += (_, _) => { if (_ready) _web.Reload(); };
        _external.Click += (_, _) => { if (NewsReaderPolicy.IsAllowed(_pending)) Ui.Platform.OpenUrl(_pending!); };
        _install.Click += (_, _) => Ui.Platform.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/#download-section");
        _install.Visibility = Visibility.Collapsed;
        _loc.LanguageChanged += Relabel;
        Relabel(this, EventArgs.Empty);
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _closed = true; _loc.LanguageChanged -= Relabel; _web.Dispose(); };
    }

    private bool _hasArticle;
    private string _statusKey = "";
    private void SetStatus(string key)
    {
        _statusKey = key;
        _status.Text = key.Length == 0 ? "" : _loc[key];
        _status.Visibility = key.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.BackgroundProperty, key is "" or "Reader_Loading" ? "InfoBarBrush" : "ErrorBarBrush");
    }

    private void Relabel(object? sender, EventArgs e)
    {
        if (!_hasArticle) Title = _loc["Reader_Title"];
        SetStatus(_statusKey);
        _back.Content = _loc["Reader_Back"]; _forward.Content = _loc["Reader_Forward"];
        _reload.Content = _loc["Reader_Reload"]; _external.Content = _loc["Reader_External"];
        _install.Content = _loc["Reader_Install"];
    }

    public void OpenArticle(string url, string title)
    {
        if (!NewsReaderPolicy.IsAllowed(url)) { SetStatus("Reader_Blocked"); return; }
        _pending = url; _address.Text = url; _hasArticle = true; Title = title;
        if (_ready) _web.CoreWebView2.Navigate(url);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(AppServices.DataDirectory.Path, "browser", "webview2"));
            if (_closed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => { e.Cancel = true; SetStatus("Reader_Blocked"); };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenArticle(e.Uri, Title); };
            core.NavigationStarting += (_, e) =>
            {
                e.Cancel = !NewsReaderPolicy.IsAllowed(e.Uri);
                SetStatus(e.Cancel ? "Reader_Blocked" : "Reader_Loading");
                if (!e.Cancel) { _pending = e.Uri; _address.Text = e.Uri; }
            };
            core.HistoryChanged += (_, _) => { _back.IsEnabled = core.CanGoBack; _forward.IsEnabled = core.CanGoForward; };
            core.DocumentTitleChanged += (_, _) => Title = core.DocumentTitle;
            core.NavigationCompleted += (_, e) => SetStatus(e.IsSuccess && e.HttpStatusCode < 400 ? "" : "Reader_Failed");
            core.ProcessFailed += (_, _) => SetStatus("Reader_Failed");
            _ready = true;
            if (_pending is not null) core.Navigate(_pending);
        }
        catch (Exception ex)
        {
            AppLog.Warn("news-reader", "WebView2 initialization failed", ex);
            if (_closed) return;
            SetStatus(ex is WebView2RuntimeNotFoundException ? "Reader_Runtime" : "Reader_Failed");
            _install.Visibility = ex is WebView2RuntimeNotFoundException ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
