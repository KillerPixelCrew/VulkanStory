using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

public sealed partial class DeltaInstallViewModel : ViewModelBase
{
    private readonly IDeltaReleaseService service;
    private readonly Func<string, bool> canRepair;
    private CancellationTokenSource? cancellation;

    public DeltaInstallViewModel(IDeltaReleaseService service, ISystemProbe probe,
        Func<string, bool>? canRepair = null)
    {
        this.service = service;
        this.canRepair = canRepair ?? DeltaRuntimeGuard.IsSeparateRuntime;
        OriginalDirectory = new GameInstallProbe(probe).Detect().FirstOrDefault()?.Directory ?? "";
        DestinationDirectory = Path.Combine(probe.HomeDirectory, "Optimum");
        DataPath = Path.Combine(probe.HomeDirectory, "OptimumData");
        if (UsesDownload) Status = "Choose your existing game folder. The exact matching Optimum release will be downloaded.";
    }

    public bool UsesDownload => service is RemoteDeltaReleaseService;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string _originalDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyPropertyChangedFor(nameof(CanRepair)),
        NotifyCanExecuteChangedFor(nameof(InstallCommand)), NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private string _destinationDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _useCustomDataPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string _dataPath = "";
    public IReadOnlyList<DeltaInstallPreset> Presets { get; } =
        [DeltaInstallPreset.ExistingSettings, DeltaInstallPreset.Desktop, DeltaInstallPreset.Handheld];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private DeltaInstallPreset _preset;

    partial void OnPresetChanged(DeltaInstallPreset value)
    {
        if (value != DeltaInstallPreset.ExistingSettings) UseCustomDataPath = true;
    }
    [ObservableProperty]
    private bool _createMenuShortcut;
    [ObservableProperty]
    private bool _createDesktopShortcut;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _accepted;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyPropertyChangedFor(nameof(CanRepair)),
        NotifyPropertyChangedFor(nameof(CanEdit)), NotifyCanExecuteChangedFor(nameof(InstallCommand)),
        NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyPropertyChangedFor(nameof(CanRepair)),
        NotifyPropertyChangedFor(nameof(CanEdit)), NotifyCanExecuteChangedFor(nameof(InstallCommand)),
        NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private bool _completed;
    [ObservableProperty]
    private string _status = "Choose your existing Vintage Story folder and a new folder for Optimum.";

    public bool CanEdit => !Busy && !Completed;
    public bool CanRepair => CanEdit && canRepair(DestinationDirectory);
    public bool CanInstall => CanEdit && Accepted && Path.IsPathFullyQualified(OriginalDirectory) &&
        Path.IsPathFullyQualified(DestinationDirectory) &&
        (!UseCustomDataPath || ValidDataPath(DataPath)) &&
        (Preset == DeltaInstallPreset.ExistingSettings ||
            (UseCustomDataPath && DeltaPresetSettings.IsEmptyDataPath(DataPath)));

    private static bool ValidDataPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || File.Exists(path)) return false;
        if (Directory.Exists(path)) return true;
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return parent is not null && Directory.Exists(parent);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (!CanInstall) return;
        // Capture before disabling editing. The worker never reads live UI state.
        string original = OriginalDirectory, destination = DestinationDirectory;
        string? dataPath = UseCustomDataPath ? DataPath : null;
        DeltaInstallPreset preset = Preset;
        ShortcutKinds shortcuts = (CreateMenuShortcut ? ShortcutKinds.Menu : ShortcutKinds.None) |
            (CreateDesktopShortcut ? ShortcutKinds.Desktop : ShortcutKinds.None);
        Busy = true;
        Status = UsesDownload
            ? "Finding and downloading the matching release, preparing your game copy and checking startup…"
            : "Verifying the release, preparing your game copy and checking startup…";
        using var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            await service.InstallAsync(original, destination, source.Token, dataPath, shortcuts, preset);
            Completed = true;
            Status = $"Optimum is ready at {destination}. Launch it from that folder. Your original installation is unchanged.";
        }
        catch (OperationCanceledException) { Status = "Installation cancelled. No new runtime was activated."; }
        catch (Exception ex) { Status = $"Installation stopped: {ex.Message}"; }
        finally { cancellation = null; Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        if (!CanRepair) return;
        string destination = DestinationDirectory;
        Busy = true;
        Status = "Rebuilding and checking the Optimum copy…";
        using var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            string backup = await service.RepairAsync(destination, source.Token);
            Completed = true;
            Status = $"Optimum is repaired at {destination}. The previous copy remains at {backup}.";
        }
        catch (OperationCanceledException) { Status = "Repair cancelled. The installed runtime remains available."; }
        catch (Exception ex) { Status = $"Repair stopped: {ex.Message}"; }
        finally { cancellation = null; Busy = false; }
    }

    [RelayCommand]
    private void Cancel() => cancellation?.Cancel();
}
