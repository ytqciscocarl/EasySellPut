using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
namespace Meili.Mac.Views.Controls;

/// <summary>Shared title/help row: one vertical center, zero paragraph margins, optional editorial page title.</summary>
public sealed class AppFormHeader : Grid
{
        public static readonly StyledProperty<string> TitleProperty=AvaloniaProperty.Register<AppFormHeader,string>(nameof(Title),"");
    public string Title{get=>GetValue(TitleProperty);set=>SetValue(TitleProperty,value);}
    public static readonly StyledProperty<string> HelpTextProperty=AvaloniaProperty.Register<AppFormHeader,string>(nameof(HelpText),"");
    public string HelpText{get=>GetValue(HelpTextProperty);set=>SetValue(HelpTextProperty,value);}
    public static readonly StyledProperty<bool> IsPageTitleProperty=AvaloniaProperty.Register<AppFormHeader,bool>(nameof(IsPageTitle),false);
    public bool IsPageTitle{get=>GetValue(IsPageTitleProperty);set=>SetValue(IsPageTitleProperty,value);}

    private readonly TextBlock _title=new(){VerticalAlignment=VerticalAlignment.Center,TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(0)};
    private readonly AppHelp _help=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(DesignTokens.SpaceSM,0,0,0)};
    public AppFormHeader()
    {
        MinHeight=DesignTokens.ControlHeight;VerticalAlignment=VerticalAlignment.Center;
        ColumnDefinitions=new ColumnDefinitions("*,Auto");
        Grid.SetColumn(_help,1);Children.Add(_title);Children.Add(_help);Update();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e){base.OnPropertyChanged(e);if(e.Property==TitleProperty||e.Property==HelpTextProperty||e.Property==IsPageTitleProperty)Update();}
    private void Update()
    {
        _title.Text=Title;_help.HelpText=HelpText;_help.IsVisible=!string.IsNullOrWhiteSpace(HelpText);
        _title.Classes.Set("PageTitle",IsPageTitle);_title.Classes.Set("CardTitle",!IsPageTitle);
        _title.Margin=new Thickness(0);
    }
}
