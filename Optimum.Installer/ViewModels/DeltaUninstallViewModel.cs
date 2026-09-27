using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

public sealed partial class DeltaUninstallViewModel : ViewModelBase
{
    private readonly IDeltaUninstallService service;

    public DeltaUninstallViewModel(IDeltaUninstallService service, string directory)
    {
        this.service = service;
        TargetDirectory = directory;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall)), NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private string _targetDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall)), NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _accepted;
    [ObservableProperty]
    private bool _launchOriginalAfterRemoval;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall)), NotifyPropertyChangedFor(nameof(CanEdit)), NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall)), NotifyPropertyChangedFor(nameof(CanEdit)), NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _completed;
    [ObservableProperty]
    private string _status = "Choose the separate Optimum installation to remove.";

    public bool CanEdit => !Busy && !Completed;
    public bool CanUninstall => CanEdit && Accepted && service.CanRemove(TargetDirectory);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!CanUninstall) return;
        string target = TargetDirectory;
        bool launchOriginal = LaunchOriginalAfterRemoval;
        Busy = true;
        Status = "Removing the Optimum copy and its shortcuts…";
        try
        {
            var result = await service.UninstallAsync(target, launchOriginal);
            if (!result.Ok)
            {
                Status = "Removal stopped: " + result.Message;
                return;
            }
            Completed = true;
            Status = (result.Message ?? "Optimum was removed.") +
                " Your original Vintage Story installation and separate data folder remain.";
        }
        catch (Exception ex) { Status = "Removal stopped: " + ex.Message; }
        finally { Busy = false; }
    }
}
