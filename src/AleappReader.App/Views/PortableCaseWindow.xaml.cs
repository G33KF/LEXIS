using System.Windows;
using System.Windows.Controls;

namespace AleappReader.App.Views;

public partial class PortableCaseWindow : Window
{
    public PortableCaseWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (DataContext is not ViewModels.PortableCaseViewModel { Running: true } vm) return;
            if (MessageBox.Show(this, "The portable case is still being written. Cancel it and close?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                vm.CancelCommand.Execute(null);
            else e.Cancel = true;
        };
    }

    void Log_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
