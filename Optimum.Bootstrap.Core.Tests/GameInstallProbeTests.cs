using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests
{
    public sealed class GameInstallProbeTests
    {
        private static void AddClient(FakeSystemProbe probe, string directory)
        {
            probe.AddDirectory(directory);
            foreach (string name in new[] { "Vintagestory.dll", "VintagestoryLib.dll", "VintagestoryAPI.dll" })
                probe.AddFile(Path.Combine(directory, name));
        }

        [Fact]
        public void FindsWindowsDefaultsAndDeduplicatesExplicitSelection()
        {
            var probe = new FakeSystemProbe { Os = OsKind.Windows };
            string root = Path.GetFullPath("test-appdata");
            probe.Environment["APPDATA"] = root;
            string directory = Path.Combine(root, "Vintagestory");
            AddClient(probe, directory);
            int reads = 0;
            var found = new GameInstallProbe(probe).Detect([directory + Path.DirectorySeparatorChar], path =>
            {
                Assert.Equal(Path.Combine(directory, "VintagestoryAPI.dll"), path);
                reads++;
                return "1.22.7";
            });
            Assert.Equal(new GameInstallation(directory, "1.22.7"), Assert.Single(found));
            Assert.Equal(1, reads);
        }

        [Fact]
        public void FindsRegistryLocationOutsideDefaultFoldersAndDeduplicatesIt()
        {
            var probe = new FakeSystemProbe { Os = OsKind.Windows };
            string directory = Path.GetFullPath("custom-vintage-story");
            AddClient(probe, directory);
            var found = new GameInstallProbe(probe).Detect([directory], _ => "1.22.7",
                [directory, Path.GetFullPath("unrelated-program")]);
            Assert.Equal(new GameInstallation(directory, "1.22.7"), Assert.Single(found));
        }

        [Fact]
        public void FindsLinuxDefaultAndPreservesUnknownVersion()
        {
            var probe = new FakeSystemProbe { HomeDirectory = Path.GetFullPath("test-home") };
            string directory = Path.Combine(probe.HomeDirectory, ".local", "share", "vintagestory");
            AddClient(probe, directory);
            Assert.Equal(new GameInstallation(directory, null),
                Assert.Single(new GameInstallProbe(probe).Detect(null, _ => null)));
        }

        [Fact]
        public void ExcludesDataDirectoriesPartialClientsAndRelativePaths()
        {
            var probe = new FakeSystemProbe();
            string partial = Path.GetFullPath("partial-client");
            probe.AddDirectory(partial).AddFile(Path.Combine(partial, "VintagestoryAPI.dll"));
            AddClient(probe, "relative-client");
            Assert.Empty(new GameInstallProbe(probe).Detect([partial, "relative-client", ""],
                _ => throw new InvalidOperationException("Must not inspect invalid candidates")));
        }

        [Fact]
        public void ReadsExactPrereleaseLiteralFromMetadata()
        {
            Assert.Equal("1.23.0-pre.2", GameInstallProbe.ReadVersion(typeof(GameInstallProbeTests).Assembly.Location));
            Assert.Null(GameInstallProbe.ReadVersion(typeof(GameInstallProbe).Assembly.Location));
        }

        [Fact]
        public void UnreadableOrInvalidAssemblyHasUnknownVersion()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "not an assembly");
                Assert.Null(GameInstallProbe.ReadVersion(path));
            }
            finally { File.Delete(path); }
            Assert.Null(GameInstallProbe.ReadVersion(path));
        }
    }
}

// A metadata fixture, not a game reference. Reading it must not run this initializer.
namespace Vintagestory.API.Config
{
    internal static class GameVersion
    {
        public const string ShortGameVersion = "1.23.0-pre.2";
        static GameVersion() => throw new InvalidOperationException("Game code must not execute");
    }
}
