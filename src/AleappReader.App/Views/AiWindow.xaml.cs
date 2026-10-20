using System.Windows;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

public partial class AiWindow : Window
{
    public AiWindow()
    {
        InitializeComponent();
        // Nothing keeps sending once the window is gone (closed, or with the main window on exit).
        Closed += (_, _) => (DataContext as AiViewModel)?.CancelCommand.Execute(null);
    }
}
