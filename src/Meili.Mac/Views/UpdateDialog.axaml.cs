using Meili.Mac.Views.Controls;
namespace Meili.Mac.Views;
public partial class UpdateDialog : AppDialog
{
    public UpdateDialog() { InitializeComponent(); }
    public UpdateDialog(UpdateViewModel updates) : this() { DataContext = updates; }
}
