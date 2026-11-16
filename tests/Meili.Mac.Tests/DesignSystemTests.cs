using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views.Controls;

namespace Meili.Mac.Tests;

public sealed class DesignSystemTests
{
    [AvaloniaFact]
    public void Select_refreshes_localized_labels_without_changing_selection()
    {
        MacHost.EnsureComposed();var item=new ComboBoxItem{Content="Original"};var select=new AppSelect();select.Items.Add(item);select.SelectedIndex=0;
        var window=new Window{Content=select};window.Show();MacHost.Pump();
        try{Assert.Equal("Original",select.SelectionBoxItem);item.Content="Updated";MacHost.Pump();Assert.Equal("Updated",select.SelectionBoxItem);Assert.Equal(0,select.SelectedIndex);}
        finally{window.Close();}
    }

    [AvaloniaTheory]
    [InlineData(false,"en-US")]
    [InlineData(true,"zh-CN")]
    public async Task Reusable_components_cover_states_drawer_modal_and_reduced_motion(bool dark,string language)
    {
        MacHost.EnsureComposed();var theme=AppServices.Get<MacTheme>();var loc=LocalizationService.Current;
        theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);loc.Apply(language);
        var zh=language=="zh-CN";
        string T(string cn,string en)=>zh?cn:en;
        var root=new Grid{ColumnDefinitions=new ColumnDefinitions("*,280"),Margin=new Thickness(24)};
        var content=new StackPanel{Spacing=16,Margin=new Thickness(0,0,24,0)};root.Children.Add(content);
        content.Children.Add(new TextBlock{Text=T("组件系统","Component system"),Classes={"PageTitle"}});
        content.Children.Add(new TextBlock{Text=T("按钮 · 输入 · 选择 · 卡片 · 抽屉 · 弹窗","Buttons · Inputs · Selects · Cards · Drawers · Dialogs"),Classes={"Caption"}});
        AppCard Card(string title,params Control[] children)
        {
            var body=new StackPanel{Spacing=12};body.Children.Add(new TextBlock{Text=title,Classes={"CardTitle"}});
            foreach(var c in children)body.Children.Add(c);var card=new AppCard{Child=body};content.Children.Add(card);return card;
        }
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};
        foreach(var pair in new[]{(T("主要操作","Primary"),"Primary"),(T("次要操作","Secondary"),""),(T("轻量操作","Ghost"),"Ghost"),(T("移除","Remove"),"Danger")})
        {
            var b=new Button{Content=pair.Item1,Margin=new Thickness(0,0,8,4)};if(pair.Item2.Length>0)b.Classes.Add(pair.Item2);actions.Children.Add(b);
        }
        actions.Children.Add(new Button{Content=T("不可用","Disabled"),IsEnabled=false,Margin=new Thickness(0,0,8,4)});
        actions.Children.Add(new Button{Content=new LucideIcon{Kind="Add"},Classes={"Icon"},Margin=new Thickness(0,0,8,4)});
        Card(T("操作","Actions"),actions);
        var input=new TextBox{PlaceholderText=T("证券代码","Symbol"),Text="AAPL"};
        var select=new AppSelect{ItemsSource=new[]{T("日线","Daily"),T("周线","Weekly"),T("月线","Monthly")},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var invalid=new TextBox{PlaceholderText=T("请输入地址","Enter an address"),Text="https://",Classes={"error"}};
        Card(T("表单","Form"),new AppFormHeader{Title=T("界面语言","Interface language"),HelpText=T("统一对齐的表单标题与帮助。","Aligned form label and help.")},input,select,invalid);
        var loading=new AppLoading();var loadingRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,Children={loading,new TextBlock{Text=T("加载中","Loading"),Classes={"Caption"},VerticalAlignment=VerticalAlignment.Center}}};
        var statusLabel=new TextBlock{Text=T("休市 · 0/8","Closed · 0/8"),Classes={"StatusText"}};
        var statusRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,Children={new Button{Classes={"StatusEntry"},Content=statusLabel},new TextBlock{Text="ET 09:30 · BJ 22:30",Classes={"StatusText"}}}};
        Card(T("状态","States"),new CheckBox{Content=T("显示新闻","Show news"),IsChecked=true},new ToggleSwitch{Content=T("自动刷新","Auto refresh"),IsChecked=true},loadingRow,statusRow);
        var drawerInput=new TextBox{Text="20"};var drawer=new AppDrawer{ReturnFocusTarget=(Button)actions.Children[0],IsOpen=true,Padding=new Thickness(20),Child=new StackPanel{Spacing=16,Children={new TextBlock{Text=T("指标","Indicators"),Classes={"SectionTitle"},Margin=default},new CheckBox{Content="MA",IsChecked=true},drawerInput,new TextBlock{Text=T("Esc 关闭","Esc to close"),Classes={"Caption"}}}}};Grid.SetColumn(drawer,1);root.Children.Add(drawer);
        var window=new Window{Width=1120,Height=820,Content=root};window.Show();MacHost.Pump();
        try
        {
            Assert.Equal(Color.FromUInt32(ThemePalette.For(dark).Card),((ISolidColorBrush)((AppCard)content.Children[2]).Background!).Color);
            Assert.Equal(DesignTokens.ControlHeight,input.MinHeight);Assert.Equal(DesignTokens.ControlHeight,select.MinHeight);
            Assert.Contains("Source Sans 3",window.FontFamily.Name);
            MacHost.Save(window,$"components-{language}-{dark}");
            drawerInput.Focus();window.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);MacHost.Pump();Assert.False(drawer.IsOpen);Assert.False(drawer.IsVisible);Assert.True(((Button)actions.Children[0]).IsFocused);
            drawer.IsOpen=true;select.IsDropDownOpen=true;MacHost.Pump();MacHost.Save(window,$"components-select-{language}-{dark}");select.IsDropDownOpen=false;
            DesignMotion.SetReduced(true);Assert.False(loading.GetVisualDescendants().OfType<ProgressBar>().Single().IsIndeterminate);
            DesignMotion.SetReduced(false);Assert.True(loading.GetVisualDescendants().OfType<ProgressBar>().Single().IsIndeterminate);
            var dialog=new AppDialog{Title=T("添加网页","Add website"),Content=new StackPanel{Spacing=16,Margin=new Thickness(24),Children={new TextBlock{Text=T("添加网页","Add website"),Classes={"SectionTitle"},Margin=default},new TextBox{PlaceholderText=T("名称","Name"),Text="Research"},new TextBox{PlaceholderText="URL",Text="https://example.com"},new Button{Content=T("添加","Add"),Classes={"Primary"},HorizontalAlignment=HorizontalAlignment.Right}}}};
            var shown=dialog.ShowDialog<bool>(window);MacHost.Pump();MacHost.Save(dialog,$"components-modal-{language}-{dark}");dialog.Close(true);Assert.True(await shown);
        }
        finally{window.Close();DesignMotion.SetReduced(false);theme.Apply(AppThemeMode.System);loc.Apply("zh-CN");}
    }
}
