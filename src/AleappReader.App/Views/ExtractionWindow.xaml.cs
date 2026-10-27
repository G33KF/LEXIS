using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

public partial class ExtractionWindow : Window
{
    public ExtractionWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ExtractionViewModel vm) vm.RequestClose += () => { DialogResult = true; };
        };
        Closing += (_, e) =>
        {
            if (DataContext is ExtractionViewModel { Running: true } running &&
                MessageBox.Show(this, $"{running.ToolName} is still running. Stop it and close?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                e.Cancel = true;
            else if (DataContext is ExtractionViewModel vm) vm.CancelCommand.Execute(null);
        };
    }

    // PasswordBox.Password is not bindable by design.
    void Pw_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExtractionViewModel vm) vm.Password = Pw.Password;
    }

    void Log_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
