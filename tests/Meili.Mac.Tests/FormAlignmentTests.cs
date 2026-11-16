using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views.Controls;
namespace Meili.Mac.Tests;
public sealed class FormAlignmentTests
{
    private static double Center(Control c,Visual root){var p=c.TranslatePoint(new Point(0,c.Bounds.Height/2),root);Assert.NotNull(p);return p!.Value.Y;}
    [AvaloniaFact] public async Task Settings_labels_help_and_fields_share_centers_in_both_languages_themes_and_sizes()
    {
        MacHost.EnsureComposed();var w=new MainWindow{Height=960};w.Show();w.Navigate(AppPage.Settings);
        try
        {
            foreach(var language in new[]{"zh-CN","en-US"})foreach(var dark in new[]{false,true})foreach(var width in new[]{980,1440})
            {
                LocalizationService.Current.Apply(language);AppServices.Get<MacTheme>().Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);w.Width=width;await Task.Delay(30);MacHost.Pump();
                var page=w.CurrentView!;var headers=page.GetVisualDescendants().OfType<AppFormHeader>().ToList();Assert.Equal(9,headers.Count);
                foreach(var header in headers)
                {
                    var title=header.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==header.Title);
                    var help=header.GetVisualDescendants().OfType<AppHelp>().Single();
                    Assert.InRange(Math.Abs(Center(title,w)-Center(help,w)),0,1);
                    var card=header.GetVisualAncestors().OfType<AppCard>().First();
                    var field=card.GetVisualDescendants().FirstOrDefault(c=>c is AppSelect or TextBox);if(field is null)continue;
                    Assert.True(Math.Abs(Center(title,w)-Center((Control)field,w))<=1,$"{header.Title}: title center {Center(title,w)}, field center {Center((Control)field,w)}, header {header.Bounds}, parent {header.Parent?.GetType().Name} {((Control?)header.Parent)?.Bounds}, card {card.Bounds}; label ancestors "+string.Join(";",header.GetVisualAncestors().OfType<Control>().Take(4).Select(c=>$"{c.GetType().Name}:{c.Bounds}:{c.VerticalAlignment}")));
                    Assert.Equal(DesignTokens.FormFieldWidth,field.Bounds.Width);
                }
                MacHost.Save(w,$"form-alignment-{width}-{language}-{dark}");
            }
            var vm=(SettingsViewModel)w.CurrentView!.DataContext!;var saved=vm.FixedRateText;
            try
            {
                vm.FixedRateText="invalid";MacHost.Pump();Assert.NotEmpty(vm.FixedRateError);
                var field=w.CurrentView.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetAutomationId(t)=="SettingsFixedRate");
                var header=field.GetVisualAncestors().OfType<AppCard>().First().GetVisualDescendants().OfType<AppFormHeader>().Single();
                var title=header.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==header.Title);Assert.InRange(Math.Abs(Center(title,w)-Center(field,w)),0,1);
            }
            finally{vm.FixedRateText=saved;}
            foreach(var pageName in new[]{AppPage.Watchlist,AppPage.Options,AppPage.Strategy,AppPage.News,AppPage.Alerts,AppPage.DataSources})
            {
                w.Navigate(pageName);await Task.Delay(40);MacHost.Pump();
                foreach(var header in w.CurrentView!.GetVisualDescendants().OfType<AppFormHeader>())
                {
                    var title=header.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==header.Title);var help=header.GetVisualDescendants().OfType<AppHelp>().Single();
                    Assert.InRange(Math.Abs(Center(title,w)-Center(help,w)),0,1);
                }
            }
        }
        finally{w.AllowClose();w.Close();LocalizationService.Current.Apply("zh-CN");}
    }
}
