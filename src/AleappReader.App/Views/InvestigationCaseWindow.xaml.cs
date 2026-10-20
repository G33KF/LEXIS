using System.Windows;

namespace AleappReader.App.Views;

public partial class InvestigationCaseWindow : Window
{
    public InvestigationCaseWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ViewModels.InvestigationCaseViewModel vm) vm.Done += () => DialogResult = true;
        };
    }

    // a label is edited on the first click, not after a second one on the selected row
    void Grid_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.DataGridCell { IsEditing: false, IsReadOnly: false } && sender is System.Windows.Controls.DataGrid g)
            g.BeginEdit(e);
    }
}
