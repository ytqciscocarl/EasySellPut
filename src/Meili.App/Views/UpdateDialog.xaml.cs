using Meili.App.Views.Controls;
namespace Meili.App.Views;
public partial class UpdateDialog : AppDialog
{
    public UpdateDialog() { InitializeComponent(); }
    public UpdateDialog(UpdateViewModel updates) : this() { DataContext = updates; }
}
