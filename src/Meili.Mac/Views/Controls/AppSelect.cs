using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Meili.Mac.Views.Controls;

/// <summary>Standard select behavior, with a live label when a localized selected item changes.</summary>
public sealed class AppSelect : ComboBox
{
    private ContentControl? _observed;
    private bool _attached;
    protected override Type StyleKeyOverride=>typeof(ComboBox);
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e){base.OnAttachedToVisualTree(e);_attached=true;Observe();}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){_attached=false;Unobserve();base.OnDetachedFromVisualTree(e);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change){base.OnPropertyChanged(change);if(change.Property==SelectedItemProperty && _attached)Observe();}
    private void Unobserve(){if(_observed is not null)_observed.PropertyChanged-=SelectedContentChanged;_observed=null;}
    private void Observe(){Unobserve();_observed=SelectedItem as ContentControl;if(_observed is not null){_observed.PropertyChanged+=SelectedContentChanged;RefreshLabel();}}
    private void SelectedContentChanged(object? sender,AvaloniaPropertyChangedEventArgs e){if(e.Property==ContentControl.ContentProperty)RefreshLabel();}
    private void RefreshLabel()
    {
        // Preserve the native visual brush path for control-valued items and all custom item templates.
        if(_observed?.Content is not Control && SelectionBoxItemTemplate is null)SelectionBoxItem=_observed?.Content;
    }
}
