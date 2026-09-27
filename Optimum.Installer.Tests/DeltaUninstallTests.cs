using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using Xunit;

namespace Optimum.Installer.Tests;

public sealed class DeltaUninstallTests
{
    private sealed class Service(Func<Task<UninstallResult>> run) : IDeltaUninstallService
    {
        public bool LaunchOriginal { get; private set; }
        public bool CanRemove(string directory) => directory == "ready";
        public Task<UninstallResult> UninstallAsync(string directory, bool launchOriginal = false)
        {
            LaunchOriginal = launchOriginal;
            return run();
        }
    }

    [Fact]
    public async Task RequiresConfirmationAndReportsFailureWithoutCompleting()
    {
        var completion = new TaskCompletionSource<UninstallResult>();
        var model = new DeltaUninstallViewModel(new Service(() => completion.Task), "ready");
        Assert.False(model.CanUninstall);
        model.Accepted = true;
        var running = model.UninstallCommand.ExecuteAsync(null);
        Assert.True(model.Busy);
        Assert.False(model.CanEdit);
        completion.SetResult(UninstallResult.Failure(FailureReason.EngineInternal, "locked file"));
        await running;
        Assert.False(model.Completed);
        Assert.True(model.CanUninstall);
        Assert.Contains("locked file", model.Status);
    }

    [AvaloniaFact]
    public void WindowRequiresAValidTargetAndConfirmation()
    {
        var model = new DeltaUninstallViewModel(new Service(() => Task.FromResult(UninstallResult.Success(1))), "not-ready");
        var window = new DeltaUninstallWindow { DataContext = model };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Content as string == "Remove Optimum");
            Assert.False(button.IsEffectivelyEnabled);
            model.TargetDirectory = "ready";
            model.Accepted = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task PassesOriginalLaunchChoiceToRemovalService()
    {
        var service = new Service(() => Task.FromResult(UninstallResult.Success(1)));
        var model = new DeltaUninstallViewModel(service, "ready")
        {
            Accepted = true, LaunchOriginalAfterRemoval = true,
        };
        await model.UninstallCommand.ExecuteAsync(null);
        Assert.True(service.LaunchOriginal);
        Assert.True(model.Completed);
    }

    [Fact]
    public async Task RemovesOnlyASeparateDeltaCopy()
    {
        string root = Directory.CreateTempSubdirectory("delta-uninstall-test-").FullName;
        string original = Path.Combine(root, "original");
        string data = Path.Combine(root, "data");
        string runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(original, "assets"));
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(runtime, ".optimum"));
        File.WriteAllText(Path.Combine(original, "keep.txt"), "vanilla");
        File.WriteAllText(Path.Combine(original, "Vintagestory.dll"), "game marker");
        string originalLauncher = Path.Combine(original, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory");
        File.WriteAllText(originalLauncher, "launcher marker");
        File.WriteAllText(Path.Combine(data, "world.txt"), "world");
        File.WriteAllText(Path.Combine(runtime, "Optimum.exe"), "launcher");
        var receipt = new DeltaRuntimeReceipt(original,
            new BinaryDeltaManifest(BinaryDeltaPack.Format, "1.22.7", "0.3.17", "win-x64", []));
        File.WriteAllText(Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.17", InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = runtime, DataPath = data,
            Launcher = Path.Combine(runtime, "Optimum.exe"), Entries = ["Optimum.exe"],
        };
        File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath), manifest.Serialize());
        try
        {
            string? launched = null;
            var service = new DeltaUninstallService(path => { launched = path; return true; });
            Assert.False(service.CanRemove(original));

            WriteReceipt(runtime, receipt with { OriginalDirectory = root });
            Assert.False(service.CanRemove(runtime));
            WriteReceipt(runtime, receipt with { OriginalDirectory = Path.GetPathRoot(root)! });
            Assert.False(service.CanRemove(runtime));
            WriteReceipt(runtime, receipt);

            File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath),
                (manifest with { DataPath = Path.Combine(runtime, "data") }).Serialize());
            Assert.False(service.CanRemove(runtime));
            File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath),
                (manifest with { InstallDirectory = Path.Combine(root, "moved") }).Serialize());
            Assert.False(service.CanRemove(runtime));
            File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath),
                (manifest with { UninstallRegistryKey = UninstallRegistration.KeyPath }).Serialize());
            Assert.False(service.CanRemove(runtime));
            File.WriteAllText(Path.Combine(runtime, InstallManifest.RelativePath), manifest.Serialize());

            Assert.True(service.CanRemove(runtime));
            UninstallResult removed = await service.UninstallAsync(runtime, launchOriginal: true);
            Assert.True(removed.Ok);
            Assert.Equal(originalLauncher, launched);
            Assert.False(Directory.Exists(runtime));
            Assert.Equal("vanilla", File.ReadAllText(Path.Combine(original, "keep.txt")));
            Assert.Equal("world", File.ReadAllText(Path.Combine(data, "world.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteReceipt(string runtime, DeltaRuntimeReceipt receipt) =>
        File.WriteAllText(Path.Combine(runtime, DeltaRuntimeInstaller.ReceiptPath),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
