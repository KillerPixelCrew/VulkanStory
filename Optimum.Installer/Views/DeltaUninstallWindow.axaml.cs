using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class DeltaUninstallWindow : Window
{
    public DeltaUninstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, e) =>
        {
            if (DataContext is DeltaUninstallViewModel { Busy: true }) e.Cancel = true;
        };
    }

    private async void BrowseTarget(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Optimum installation folder", AllowMultiple = false,
        });
        if (DataContext is DeltaUninstallViewModel { CanEdit: true } model &&
            folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            model.TargetDirectory = path;
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
