using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

public partial class AiSettingsWindow : Window
{
    public AiSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is AiSettingsViewModel vm) vm.Done += () => DialogResult = true;
        };
        Loaded += async (_, _) => { if (DataContext is AiSettingsViewModel vm) await vm.DetectLocal(); };
    }

    /// <summary>A PasswordBox has no bindable text: the typed key goes to its row, which encrypts it on save.</summary>
    void Key_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AiProviderRow row } box) row.NewKey = box.Password == "" ? null : box.Password;
    }
}
