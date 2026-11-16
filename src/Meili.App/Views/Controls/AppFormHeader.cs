using System.Windows;
using System.Windows.Controls;
namespace Meili.App.Views.Controls;

/// <summary>Shared title/help row: one vertical center, zero paragraph margins, optional editorial page title.</summary>
public sealed class AppFormHeader : Grid
{
        public static readonly DependencyProperty TitleProperty=DependencyProperty.Register(nameof(Title),typeof(string),typeof(AppFormHeader),new PropertyMetadata("",Changed));
    public string Title{get=>(string)GetValue(TitleProperty);set=>SetValue(TitleProperty,value);}
    public static readonly DependencyProperty HelpTextProperty=DependencyProperty.Register(nameof(HelpText),typeof(string),typeof(AppFormHeader),new PropertyMetadata("",Changed));
    public string HelpText{get=>(string)GetValue(HelpTextProperty);set=>SetValue(HelpTextProperty,value);}
    public static readonly DependencyProperty IsPageTitleProperty=DependencyProperty.Register(nameof(IsPageTitle),typeof(bool),typeof(AppFormHeader),new PropertyMetadata(false,Changed));
    public bool IsPageTitle{get=>(bool)GetValue(IsPageTitleProperty);set=>SetValue(IsPageTitleProperty,value);}

    private readonly TextBlock _title=new(){VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0)};
    private readonly AppHelp _help=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(DesignTokens.SpaceSM,0,0,0)};
    public AppFormHeader()
    {
        MinHeight=DesignTokens.ControlHeight;VerticalAlignment=VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        Grid.SetColumn(_help,1);Children.Add(_title);Children.Add(_help);Update();
    }
    private static void Changed(DependencyObject o,DependencyPropertyChangedEventArgs e)=>((AppFormHeader)o).Update();
    private void Update()
    {
        _title.Text=Title;_help.HelpText=HelpText;_help.Visibility=string.IsNullOrWhiteSpace(HelpText)?Visibility.Collapsed:Visibility.Visible;
        _title.SetResourceReference(StyleProperty,IsPageTitle?"PageTitle":"CardTitle");
        _title.Margin=new Thickness(0);
    }
}
