using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Meili.Mac.Platform;

/// <summary>Unprivileged remote browsing surface. Remains attached while its rail tab is hidden.</summary>
public sealed class WebTabPane : UserControl
{
    private readonly NewsWebView _web = new();
    private readonly TextBox _address = new() { PlaceholderText = "https://", MinWidth = 80 };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 4), FontSize = 12, IsVisible = false };
    private readonly Button _back = new(), _forward = new(), _reload = new(), _external = new(), _edit = new();
    private readonly LocalizationService _loc = LocalizationService.Current;
    public event Action? EditRequested;
    public event Action? CloseRequested;
    private readonly bool _articleMode;
    private readonly TextBlock _source=new();
    public string Address => _web.Address;
    public string DocumentTitle => _web.DocumentTitle;
    public WebTabPane(string url, bool articleMode=false)
    {
        _articleMode=articleMode;
        var root = new DockPanel();
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(12, 8) };
        var controls = new Control[] { _back, _forward, _reload, _address, _external, _edit };
        for (var i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], i); toolbar.Children.Add(controls[i]); controls[i].Margin = new Thickness(0, 0, i == 5 ? 0 : 6, 0); }
        foreach (var b in new[] { _back, _forward, _reload, _external, _edit }) { b.Classes.Add("Icon"); b.Width = 32; }
        _back.Content = new Meili.Mac.Views.Controls.LucideIcon { Kind = "ArrowLeft" }; _forward.Content = new Meili.Mac.Views.Controls.LucideIcon { Kind = "ArrowRight" }; _reload.Content = new Meili.Mac.Views.Controls.LucideIcon { Kind = "Refresh" }; _external.Content = new Meili.Mac.Views.Controls.LucideIcon { Kind = "ExternalLink" }; _edit.Content = new Meili.Mac.Views.Controls.LucideIcon { Kind = "More" };
        if(articleMode)
        {
            toolbar.Children.Clear();toolbar.ColumnDefinitions.Clear();
            toolbar.ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto,Auto");
            _source.FontSize=12;_source.VerticalAlignment=VerticalAlignment.Center;_source.TextTrimming=Avalonia.Media.TextTrimming.CharacterEllipsis;
            var items=new Control[]{_source,_reload,_external,_edit};
            for(var i=0;i<items.Length;i++){Grid.SetColumn(items[i],i);toolbar.Children.Add(items[i]);}
            _edit.Content=new Meili.Mac.Views.Controls.LucideIcon {Kind="Close"};
        }
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status); root.Children.Add(_web); Content = root;
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
        _back.Click += (_, _) => _web.Back(); _forward.Click += (_, _) => _web.Forward(); _reload.Click += (_, _) => _web.Reload();
        _external.Click += (_, _) => { if (NewsReaderPolicy.IsAllowed(Address)) Ui.Platform.OpenUrl(Address); };
        _edit.Click += (_, _) => {if(_articleMode)CloseRequested?.Invoke();else EditRequested?.Invoke();};
        _address.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; NavigateAddress(); e.Handled = true; };
        _web.Changed += Update;
        AttachedToVisualTree += (_, _) => { _loc.LanguageChanged += Relabel; Relabel(this, EventArgs.Empty); };
        DetachedFromVisualTree += (_, _) => _loc.LanguageChanged -= Relabel;
        _address.Text = url; _web.Navigate(url); Update();
    }
    public bool IsPresentationPolling=>_web.IsPresentationPolling;
    public void SetPresentationActive(bool active)=>_web.SetPresentationActive(active);
    public void OpenArticle(string url){if(!NewsReaderPolicy.IsAllowed(url))throw new ArgumentException("Unsupported article URL",nameof(url));_web.Navigate(url);Update();}
    public void ReloadPage() => _web.Reload();
    public void FocusAddress() { _address.Focus(); _address.SelectAll(); }
    private void NavigateAddress()
    {
        if (WebWorkspace.TryNormalize("Web", _address.Text, out _, out var url)) _web.Navigate(url);
        else { _status.Text = _loc["Web_Invalid"]; _status.IsVisible = true; }
    }
    private void Relabel(object? sender, EventArgs e)
    {
        var buttons = new[] { _back, _forward, _reload, _external, _edit };
        var keys = new[] { "Reader_Back", "Reader_Forward", "Reader_Reload", "Reader_External", _articleMode ? "Reader_Close" : "Web_Edit" };
        for (var i = 0; i < buttons.Length; i++) { ToolTip.SetTip(buttons[i], _loc[keys[i]]); AutomationProperties.SetName(buttons[i], _loc[keys[i]]); }
        AutomationProperties.SetName(_address, _loc["Web_Address"]); Update();
    }
    private void Update()
    {
        if (!_address.IsKeyboardFocusWithin && NewsReaderPolicy.IsAllowed(Address)) _address.Text = Address;
        if(_articleMode&&Uri.TryCreate(Address,UriKind.Absolute,out var destination))
        {
            _source.Text=destination.Host;
            ToolTip.SetTip(_source,DocumentTitle+"\n"+Address);
        }
        _back.IsEnabled = _web.CanGoBack; _forward.IsEnabled = _web.CanGoForward;
        _status.Text = _web.StatusKey.Length == 0 ? "" : _loc[_web.StatusKey]; _status.IsVisible = _status.Text.Length > 0;
    }
}
