using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class DeltaInstallWindow : Window
{
    public DeltaInstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, e) =>
        {
            if (DataContext is DeltaInstallViewModel { Busy: true } model)
            {
                e.Cancel = true;
                model.CancelCommand.Execute(null);
            }
        };
    }

    private async void BrowseOriginal(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Vintage Story folder", AllowMultiple = false });
        if (DataContext is DeltaInstallViewModel { CanEdit: true } model && folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            model.OriginalDirectory = path;
    }

    private async void BrowseDestination(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the parent folder for Optimum", AllowMultiple = false });
        if (DataContext is DeltaInstallViewModel { CanEdit: true } model && folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            model.DestinationDirectory = Path.Combine(path, "Optimum");
    }

    private async void BrowseDataPath(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Existing Vintage Story data folder", AllowMultiple = false });
        if (DataContext is DeltaInstallViewModel { CanEdit: true, UseCustomDataPath: true } model &&
            folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            model.DataPath = path;
    }

    private async void BrowseRepair(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Existing Optimum folder to repair", AllowMultiple = false,
        });
        if (DataContext is DeltaInstallViewModel { CanEdit: true } model &&
            folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            model.DestinationDirectory = path;
    }

    private void OpenUninstall(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeltaInstallViewModel { CanEdit: true } model) return;
        new DeltaUninstallWindow
        {
            DataContext = new DeltaUninstallViewModel(new DeltaUninstallService(), model.DestinationDirectory),
        }.Show(this);
    }
}
