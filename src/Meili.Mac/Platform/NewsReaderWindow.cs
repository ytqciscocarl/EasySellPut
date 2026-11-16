using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Meili.Mac.Platform;

public sealed class NewsReaderWindow : Window
{
    private readonly NewsWebView _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox _address = new() { IsReadOnly = true };
    private readonly Button _back = new(), _forward = new(), _reload = new(), _external = new();
    private readonly LocalizationService _loc = LocalizationService.Current;
    private string? _url;
    public NewsReaderWindow()
    {
        Width = 1050; Height = 760; MinWidth = 640; MinHeight = 400;
        FontSize = 14;
        this.Bind(BackgroundProperty, new DynamicResourceExtension("ApplicationBackgroundBrush"));
        this.Bind(ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"));
        this.Bind(FontFamilyProperty, new DynamicResourceExtension("AppFont"));
        _back.IsEnabled = _forward.IsEnabled = false;
        _address.Margin = new Thickness(12, 0, 12, 8);
        _address.Padding = new Thickness(10, 6);
        _address.Bind(TextBox.BackgroundProperty, new DynamicResourceExtension("ControlFillColorSecondaryBrush"));
        _address.Bind(TextBox.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"));
        _address.Bind(TextBox.BorderBrushProperty, new DynamicResourceExtension("ControlStrokeColorDefaultBrush"));
        _status.Margin = new Thickness(12, 0, 12, 8);
        _status.Padding = new Thickness(12, 8);
        _status.FontSize = 12;
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
        _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("InfoBarBrush"));
        _status.IsVisible = false;
        var root = new DockPanel();
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 6, 2) };
        foreach (var button in new[] { _back, _forward, _reload, _external })
        {
            button.Margin = new Thickness(0, 0, 6, 6); button.Padding = new Thickness(12, 6);
            button.FontSize = 14; button.MinHeight = 32; button.CornerRadius = new CornerRadius(6);
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_address, Dock.Top); root.Children.Add(_address);
        DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        root.Children.Add(_web); Content = root;
        _back.Click += (_, _) => _web.Back(); _forward.Click += (_, _) => _web.Forward();
        _reload.Click += (_, _) => _web.Reload();
        _external.Click += (_, _) => { if (NewsReaderPolicy.IsAllowed(_url)) Ui.Platform.OpenUrl(_url!); };
        _web.Changed += Update;
        _loc.LanguageChanged += Relabel;
        Relabel(this, EventArgs.Empty);
        Closed += (_, _) => { _loc.LanguageChanged -= Relabel; _web.Changed -= Update; };
    }
    public void OpenArticle(string url, string title)
    {
        if (!NewsReaderPolicy.IsAllowed(url)) { SetStatus("Reader_Blocked"); return; }
        _url = url; _address.Text = url; _hasArticle = true; Title = title; _web.Navigate(url);
    }
    private void Update()
    {
        if (NewsReaderPolicy.IsAllowed(_web.Address)) { _url = _web.Address; _address.Text = _url; }
        if (_web.DocumentTitle.Length > 0) Title = _web.DocumentTitle;
        SetStatus(_web.StatusKey);
        _back.IsEnabled = _web.CanGoBack; _forward.IsEnabled = _web.CanGoForward;
    }
    private bool _hasArticle;
    private string _statusKey = "";
    private void SetStatus(string key)
    {
        _statusKey = key;
        _status.Text = key.Length == 0 ? "" : _loc[key];
        _status.IsVisible = key.Length > 0;
        _status.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension(key is "" or "Reader_Loading" ? "InfoBarBrush" : "ErrorBarBrush"));
    }

    private void Relabel(object? sender, EventArgs e)
    {
        if (!_hasArticle) Title = _loc["Reader_Title"];
        SetStatus(_statusKey);
        _back.Content = _loc["Reader_Back"]; _forward.Content = _loc["Reader_Forward"];
        _reload.Content = _loc["Reader_Reload"]; _external.Content = _loc["Reader_External"];
    }
}
