using System.Windows;
using System.Windows.Controls;
namespace Meili.App.Views.Controls;

/// <summary>Reusable live-content split: no overlay, no WebView reparenting on resize, no layout animation.</summary>
public sealed class ArticleSplitView : Grid
{
    private readonly GridSplitter _divider=new();
    private readonly Border _article=new();
    private readonly FrameworkElement _main;
    private bool _open;
    public bool IsOpen=>_open;
    public bool IsVertical=>false;
    public ArticleSplitView(FrameworkElement main)
    {
        _main=main;Children.Add(main);Children.Add(_divider);Children.Add(_article);
        _article.SetResourceReference(Border.BackgroundProperty,"ApplicationBackgroundBrush");
        _divider.Background=System.Windows.Media.Brushes.Transparent;
        _article.SetResourceReference(Border.BorderBrushProperty,"ControlStrokeColorDefaultBrush");
        _divider.HorizontalAlignment=HorizontalAlignment.Stretch;_divider.VerticalAlignment=VerticalAlignment.Stretch;
        SetOpen(false);
    }
    public void Open(FrameworkElement article){_article.Child=article;if(!_open)SetOpen(true);}
    public void Close(){_article.Child=null;SetOpen(false);}
    private void SetOpen(bool open)
    {
        _open=open;_divider.Visibility=_article.Visibility=open?Visibility.Visible:Visibility.Collapsed;
        LayoutSplit();
    }
    private void LayoutSplit()
    {
        // Stable definition objects keep native views attached and retain the ratio on article changes.
        if(ColumnDefinitions.Count==0)for(var i=0;i<3;i++)ColumnDefinitions.Add(new ColumnDefinition());
        if(RowDefinitions.Count==0)RowDefinitions.Add(new RowDefinition());
        _article.BorderThickness=new Thickness(1,0,0,0);
        SetColumn(_main,0);SetRow(_main,0);SetColumn(_divider,1);SetRow(_divider,0);SetColumn(_article,2);SetRow(_article,0);
        ColumnDefinitions[0].Width=new GridLength(_open?5:1,GridUnitType.Star);ColumnDefinitions[0].MinWidth=_open?500:0;
        ColumnDefinitions[1].Width=new GridLength(_open?6:0);
        ColumnDefinitions[2].Width=_open?new GridLength(4,GridUnitType.Star):new GridLength(0);ColumnDefinitions[2].MinWidth=_open?340:0;
        _divider.ResizeDirection=GridResizeDirection.Columns;
    }
}
