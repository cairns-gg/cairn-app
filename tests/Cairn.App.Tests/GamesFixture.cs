using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Cairn.App.ViewModels;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Runtime;

namespace Cairn.App.Tests;

/// <summary>
/// A GamesViewModel over a temp store, with no network and a system install we choose.
///
/// Built directly rather than through MainViewModel because the interesting cases —
/// "the machine has its own 1.22.5" — cannot be arranged by whatever happens to be
/// installed on the machine running the tests.
/// </summary>
public static class Games
{
    /// <summary>
    /// Where an install of <paramref name="name"/> belongs under a games root.
    ///
    /// Asked of GameStore rather than composed by hand: on macOS an install directory is a
    /// bundle, and a path built here without that suffix is one nothing in Cairn looks in —
    /// so the test arranges a fixture the code cannot see and then asserts about its absence.
    /// </summary>
    public static string DirIn(string gamesRoot, string name) =>
        new GameStore(gamesRoot).InstallDir(name);

    public static GameInstall FakeInstall(string version, string dir, int bytes = 0)
    {
        // GameInstall.TryAt only requires that these two exist, so a directory of empty
        // files is a real enough install for everything below the launch itself.
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ExecutableName), new byte[bytes]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        return new GameInstall
        {
            Directory = dir,
            Executable = Path.Combine(dir, ExecutableName),
            Version = version,
            Architecture = ExecutableArch.X64,
            RequiredFramework = new Version(10, 0, 0),
        };
    }

    private static string ExecutableName =>
        OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory";

    public sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http = new(new OfflineHandler());
        private readonly GameStore _store;
        private readonly string _root;

        public Fixture(
            string home, GameInstall? system, IReadOnlyList<string> packsUsingIt,
            string? storeRoot = null)
        {
            _root = storeRoot ?? Path.Combine(home, "games-" + Guid.NewGuid().ToString("n")[..6]);
            _store = new GameStore(_root);

            Vm = new GamesViewModel(
                _http, _store, new RuntimeStore(Path.Combine(home, "runtimes")),
                log: _ => { }, onLibraryChanged: () => { },
                system: system,
                // Which packs target which version is MainViewModel's one-line filter; what
                // matters here is what the prompt does with the answer.
                packsUsing: _ => packsUsingIt);
        }

        public GamesViewModel Vm { get; }

        /// <summary>Creates a managed install and returns its directory.</summary>
        public string AddManaged(string version)
        {
            var dir = _store.InstallDir(version);
            FakeInstall(version, dir);
            Vm.RefreshInstalled();
            return dir;
        }

        /// <summary>
        /// A managed install in a directory named something other than a version, so the
        /// store's directory-name fallback does not apply and it reports "unknown".
        /// </summary>
        public string AddManagedAt(string directoryName)
        {
            var dir = Path.Combine(_root, directoryName);
            FakeInstall("does-not-matter", dir);
            Vm.RefreshInstalled();
            return dir;
        }

        /// <summary>
        /// The install found in <paramref name="dir"/>. Its reported version is "unknown" —
        /// the fake dll carries no metadata, and the directory name is not a version either —
        /// which is exactly the case that used to make Remove delete nothing while logging
        /// success.
        /// </summary>
        public InstalledGameViewModel Managed(string dir) =>
            Vm.Installed.Single(i => i.Directory == dir);

        public void Dispose()
        {
            _http.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
