using System.Text;

namespace LibTmux.McpSwap.Tests;

public sealed class SwapServiceTests
{
    [Fact]
    public void AllEightClientsCommitAndRevertAsOneTransaction()
    {
        using SwapFixture fixture = new();
        Dictionary<string, byte[]> pristine = fixture.SeedAllClients();

        fixture.Service.Use(fixture.UseOptions());

        Assert.All(
            fixture.Catalog.Clients,
            client => Assert.Equal(
                fixture.Binary,
                fixture.ReadServer(client, ConfigScope.Project)!.Command));
        Assert.True(File.Exists(fixture.StateFile));

        fixture.Service.Revert(fixture.RevertOptions());

        foreach (ClientInfo client in fixture.Catalog.Clients)
        {
            Assert.Equal(pristine[client.Name], File.ReadAllBytes(client.ConfigPath));
        }

        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void ClaudeUserAndProjectLayersRevertInLifoOrder()
    {
        using SwapFixture fixture = new();
        fixture.Seed(fixture.Catalog["claude"], "{\"theme\":\"dark\"}");
        byte[] pristine = File.ReadAllBytes(fixture.Catalog["claude"].ConfigPath);

        fixture.Service.Use(fixture.UseOptions(["claude"]));
        string second = fixture.CreateBinary("other-mcp");
        fixture.Service.Use(fixture.UseOptions(["claude"], ConfigScope.User, second));

        Assert.Equal(fixture.Binary, fixture.ReadServer(fixture.Catalog["claude"], ConfigScope.Project)!.Command);
        Assert.Equal(second, fixture.ReadServer(fixture.Catalog["claude"], ConfigScope.User)!.Command);

        fixture.Service.Revert(fixture.RevertOptions(["claude"]));

        Assert.Equal(pristine, File.ReadAllBytes(fixture.Catalog["claude"].ConfigPath));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void DryRunPlansWithoutCreatingLockStateBackupsOrConfigWrites()
    {
        using SwapFixture fixture = new();
        fixture.Seed(fixture.Catalog["cursor"], "{}");
        byte[] pristine = File.ReadAllBytes(fixture.Catalog["cursor"].ConfigPath);

        fixture.Service.Use(fixture.UseOptions(["cursor"]) with { DryRun = true });

        Assert.Equal(pristine, File.ReadAllBytes(fixture.Catalog["cursor"].ConfigPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.StateFile)));
        Assert.Equal(
            [fixture.Catalog["cursor"].ConfigPath],
            Directory.GetFiles(Path.GetDirectoryName(fixture.Catalog["cursor"].ConfigPath)!));
    }

    [Fact]
    public void MalformedSelectedConfigBlocksEveryClientBeforePreflightOrWrite()
    {
        using SwapFixture fixture = new();
        fixture.Seed(fixture.Catalog["cursor"], "{}");
        fixture.Seed(fixture.Catalog["gemini"], [0x7b, 0xff, 0x7d]);
        byte[] pristine = File.ReadAllBytes(fixture.Catalog["cursor"].ConfigPath);

        Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor", "gemini"])));

        Assert.Equal(pristine, File.ReadAllBytes(fixture.Catalog["cursor"].ConfigPath));
        Assert.Empty(fixture.Preflight.Specs);
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void EachDistinctFinalEnvironmentIsPreflightedBeforeWriting()
    {
        using SwapFixture fixture = new();
        fixture.Seed(
            fixture.Catalog["cursor"],
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"args\":[],\"env\":{\"CLIENT\":\"cursor\"}}}}");
        fixture.Seed(
            fixture.Catalog["gemini"],
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"args\":[],\"env\":{\"CLIENT\":\"gemini\"}}}}");

        fixture.Service.Use(
            fixture.UseOptions(["cursor", "gemini"]) with { NoPreflight = false });

        Assert.Equal(2, fixture.Preflight.Specs.Count);
        Assert.Contains(fixture.Preflight.Specs, spec => spec.Environment["CLIENT"] == "cursor");
        Assert.Contains(fixture.Preflight.Specs, spec => spec.Environment["CLIENT"] == "gemini");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ToolsetsMigratesInheritedSafetyWithoutDroppingOtherEnvironment(
        bool supplyToolsets,
        bool expectSafety)
    {
        using SwapFixture fixture = new();
        fixture.Seed(
            fixture.Catalog["cursor"],
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"args\":[],\"env\":{\"LIBTMUX_SAFETY\":\"read-only\",\"KEEP\":\"yes\"}}}}");
        CommandOptions options = fixture.UseOptions(["cursor"]);
        if (supplyToolsets)
        {
            options = options with
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LIBTMUX_TOOLSETS"] = "inspect,execute",
                },
            };
        }

        fixture.Service.Use(options);

        IReadOnlyDictionary<string, string> environment = fixture.ReadServer(
            fixture.Catalog["cursor"],
            ConfigScope.User)!.Environment;
        Assert.Equal("yes", environment["KEEP"]);
        Assert.Equal(expectSafety, environment.ContainsKey("LIBTMUX_SAFETY"));
        Assert.Equal(supplyToolsets, environment.ContainsKey("LIBTMUX_TOOLSETS"));
    }

    [Fact]
    public void HardlinkedClientConfigsAreRejectedBeforeWriting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        ClientInfo gemini = fixture.Catalog["gemini"];
        fixture.Seed(cursor, "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(gemini.ConfigPath)!);
        NativeFileSystem.CreateHardLink(gemini.ConfigPath, cursor.ConfigPath);
        byte[] pristine = File.ReadAllBytes(cursor.ConfigPath);

        Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor", "gemini"])));

        Assert.Equal(pristine, File.ReadAllBytes(cursor.ConfigPath));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void SymlinkedConfigUpdatesItsTargetAndRevertsByteIdentically()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        string target = Path.Combine(fixture.Paths.Root, "shared", "cursor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{\"theme\":\"dark\"}");
        byte[] pristine = File.ReadAllBytes(target);
        Directory.CreateDirectory(Path.GetDirectoryName(cursor.ConfigPath)!);
        File.CreateSymbolicLink(cursor.ConfigPath, target);
        string link = new FileInfo(cursor.ConfigPath).LinkTarget!;

        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        Assert.Equal(fixture.Binary, fixture.ReadServer(cursor, ConfigScope.User)!.Command);
        Assert.Equal(link, new FileInfo(cursor.ConfigPath).LinkTarget);

        fixture.Service.Revert(fixture.RevertOptions(["cursor"]));
        Assert.Equal(pristine, File.ReadAllBytes(target));
        Assert.Equal(link, new FileInfo(cursor.ConfigPath).LinkTarget);
    }

    [Fact]
    public void HumanEditBlocksRevertAndPreservesEveryRecoveryArtifact()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        string state = File.ReadAllText(fixture.StateFile);
        string backup = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"].BackupPath;
        File.AppendAllText(cursor.ConfigPath, "\n");
        byte[] edited = File.ReadAllBytes(cursor.ConfigPath);

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(edited, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal(state, File.ReadAllText(fixture.StateFile));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void LateFailureRollsBackEveryEarlierClient()
    {
        using SwapFixture fixture = new();
        Dictionary<string, byte[]> pristine = fixture.SeedAllClients();
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-config:gemini")
            {
                throw new IOException("injected late failure");
            }
        };

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions()));

        foreach (ClientInfo client in fixture.Catalog.Clients)
        {
            Assert.Equal(pristine[client.Name], File.ReadAllBytes(client.ConfigPath));
        }

        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void SamePathReplacementBeforeCommitIsNotOverwritten()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] human = Encoding.UTF8.GetBytes("{\"human\":true}");
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-config:cursor")
            {
                File.Delete(cursor.ConfigPath);
                File.WriteAllBytes(cursor.ConfigPath, human);
            }
        };

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Equal(human, File.ReadAllBytes(cursor.ConfigPath));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void RecoveryStateAndBackupsAreDotnetNamespacedUnderTheSharedLock()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");

        fixture.Service.Use(fixture.UseOptions(["cursor"]));

        RecoveryEntry entry = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        Assert.Contains(
            Path.Combine("libtmux-mcp-dev", "swap", "dotnet"),
            fixture.StateFile,
            StringComparison.Ordinal);
        Assert.Contains(".bak.mcp-swap-dotnet-", entry.BackupPath, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.Runtime.LockFile));
        Assert.Equal(Path.Combine(fixture.Runtime.SwapDirectory, "state.lock"), fixture.Runtime.LockFile);
    }

    [Fact]
    public void DryRunRejectsAnUnsafePersistentLockWithoutWriting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] pristine = File.ReadAllBytes(cursor.ConfigPath);
        Directory.CreateDirectory(fixture.Runtime.SwapDirectory);
        File.SetUnixFileMode(
            fixture.Runtime.SwapDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(fixture.Runtime.LockFile, string.Empty);
        File.SetUnixFileMode(
            fixture.Runtime.LockFile,
            UnixFileMode.UserRead | UnixFileMode.GroupRead);

        Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor"]) with { DryRun = true }));

        Assert.Equal(pristine, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Empty(fixture.Provisioner.Plans);
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Theory]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    public void ConfigAliasToTheSharedLockIsRejectedWithoutWriting(string alias)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        Directory.CreateDirectory(fixture.Runtime.SwapDirectory);
        File.SetUnixFileMode(
            fixture.Runtime.SwapDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(fixture.Runtime.LockFile, "{}");
        File.SetUnixFileMode(
            fixture.Runtime.LockFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(Path.GetDirectoryName(cursor.ConfigPath)!);
        if (alias == "hardlink")
        {
            NativeFileSystem.CreateHardLink(cursor.ConfigPath, fixture.Runtime.LockFile);
        }
        else
        {
            File.CreateSymbolicLink(cursor.ConfigPath, fixture.Runtime.LockFile);
        }

        byte[] lockBytes = File.ReadAllBytes(fixture.Runtime.LockFile);

        Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor"]) with { DryRun = true }));

        Assert.Equal(lockBytes, File.ReadAllBytes(fixture.Runtime.LockFile));
        Assert.Empty(fixture.Provisioner.Plans);
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void AConfigHardlinkedToAnOlderRecoveryBackupIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        ClientInfo gemini = fixture.Catalog["gemini"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        Directory.CreateDirectory(Path.GetDirectoryName(gemini.ConfigPath)!);
        NativeFileSystem.CreateHardLink(gemini.ConfigPath, recovery.BackupPath);
        byte[] state = File.ReadAllBytes(fixture.StateFile);
        byte[] swapped = File.ReadAllBytes(cursor.ConfigPath);

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions(["gemini"])));

        Assert.Equal(state, File.ReadAllBytes(fixture.StateFile));
        Assert.Equal(swapped, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal(File.ReadAllBytes(recovery.BackupPath), File.ReadAllBytes(gemini.ConfigPath));
    }

    [Fact]
    public void ChecksumTamperingBlocksRevertAndPreservesRecovery()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        byte[] swapped = File.ReadAllBytes(cursor.ConfigPath);
        byte[] backup = File.ReadAllBytes(recovery.BackupPath);
        string state = File.ReadAllText(fixture.StateFile);
        File.WriteAllText(
            fixture.StateFile,
            state.Replace("\"server\": \"tmux\"", "\"server\": \"tampered\"", StringComparison.Ordinal));
        byte[] tampered = File.ReadAllBytes(fixture.StateFile);

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(swapped, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal(backup, File.ReadAllBytes(recovery.BackupPath));
        Assert.Equal(tampered, File.ReadAllBytes(fixture.StateFile));
    }

    [Fact]
    public void ModifiedBackupBlocksRevertAndPreservesEveryArtifact()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        byte[] state = File.ReadAllBytes(fixture.StateFile);
        byte[] swapped = File.ReadAllBytes(cursor.ConfigPath);
        File.AppendAllText(recovery.BackupPath, "human");
        byte[] editedBackup = File.ReadAllBytes(recovery.BackupPath);

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(swapped, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal(editedBackup, File.ReadAllBytes(recovery.BackupPath));
        Assert.Equal(state, File.ReadAllBytes(fixture.StateFile));
    }

    [Fact]
    public void ConfigSymlinkRetargetBlocksRevertWithoutTouchingEitherTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        string first = Path.Combine(fixture.Paths.Root, "targets", "first.json");
        string second = Path.Combine(fixture.Paths.Root, "targets", "second.json");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllText(first, "{}");
        File.WriteAllText(second, "{\"human\":true}");
        Directory.CreateDirectory(Path.GetDirectoryName(cursor.ConfigPath)!);
        File.CreateSymbolicLink(cursor.ConfigPath, first);
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        byte[] firstSwapped = File.ReadAllBytes(first);
        byte[] state = File.ReadAllBytes(fixture.StateFile);
        File.Delete(cursor.ConfigPath);
        File.CreateSymbolicLink(cursor.ConfigPath, second);
        byte[] secondPristine = File.ReadAllBytes(second);

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(firstSwapped, File.ReadAllBytes(first));
        Assert.Equal(secondPristine, File.ReadAllBytes(second));
        Assert.Equal(state, File.ReadAllBytes(fixture.StateFile));
    }

    [Fact]
    public void EverySelectedFinalSpecMustPassPreflightBeforeAnyWrite()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        ClientInfo gemini = fixture.Catalog["gemini"];
        fixture.Seed(
            cursor,
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"cursor\"}}}}");
        fixture.Seed(
            gemini,
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"gemini\"}}}}");
        byte[] cursorBefore = File.ReadAllBytes(cursor.ConfigPath);
        byte[] geminiBefore = File.ReadAllBytes(gemini.ConfigPath);
        fixture.Preflight.Failure = spec => spec.Environment["CLIENT"] == "gemini"
            ? "gemini rejected initialize"
            : null;

        Assert.Throws<SwapException>(() => fixture.Service.Use(
            fixture.UseOptions(["cursor", "gemini"]) with { NoPreflight = false }));

        Assert.Equal(2, fixture.Preflight.Specs.Count);
        Assert.Equal(cursorBefore, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal(geminiBefore, File.ReadAllBytes(gemini.ConfigPath));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void PreflightUsesTheFinalConfigReplannedAfterSourceSetup()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(
            cursor,
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"preview\"}}}}");
        fixture.Provisioner.OnPrepare = (_, _) => fixture.Seed(
            cursor,
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"final\"}}}}");

        fixture.Service.Use(fixture.UseOptions(["cursor"]) with { NoPreflight = false });

        ServerSpec probed = Assert.Single(fixture.Preflight.Specs);
        Assert.Equal("final", probed.Environment["CLIENT"]);
        fixture.Service.Revert(fixture.RevertOptions(["cursor"]));
        Assert.Contains("\"CLIENT\":\"final\"", File.ReadAllText(cursor.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeAfterPreflightFailsClosedOutsideTheLock()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(
            cursor,
            "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"preflight\"}}}}");
        bool lockExistedDuringPreflight = false;
        fixture.Preflight.Failure = _ =>
        {
            lockExistedDuringPreflight = File.Exists(fixture.Runtime.LockFile);
            fixture.Seed(
                cursor,
                "{\"mcpServers\":{\"tmux\":{\"command\":\"old\",\"env\":{\"CLIENT\":\"raced\"}}}}");
            return null;
        };

        SwapException failure = Assert.Throws<SwapException>(() => fixture.Service.Use(
            fixture.UseOptions(["cursor"]) with { NoPreflight = false }));

        Assert.False(lockExistedDuringPreflight);
        Assert.Contains("changed after preflight", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"CLIENT\":\"raced\"", File.ReadAllText(cursor.ConfigPath), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void LateConfigReplacementIsRetainedWithTheOriginalRecoveryCopy()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] human = "{\"human\":true}"u8.ToArray();
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "after-config-publish:cursor")
            {
                File.Delete(cursor.ConfigPath);
                File.WriteAllBytes(cursor.ConfigPath, human);
            }
        };

        SwapException failure = Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Contains("recovery files retained", failure.Message, StringComparison.Ordinal);
        Assert.Equal(human, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Contains(
            Directory.GetFiles(fixture.Paths.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("mcp-swap-recovery", StringComparison.Ordinal)
                && File.ReadAllBytes(path).SequenceEqual("{}"u8.ToArray()));
    }

    [Fact]
    public void LateBackupDestinationSurvivesWhileTheConfigRollsBack()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] pristine = File.ReadAllBytes(cursor.ConfigPath);
        string? destination = null;
        fixture.Service.TransitionHook = transition =>
        {
            if (transition != "before-backup-publish:cursor")
            {
                return;
            }

            string stage = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(cursor.ConfigPath)!,
                "*.mcp-swap-backup-*"));
            string name = Path.GetFileName(stage);
            int marker = name.IndexOf(".mcp-swap-backup-", StringComparison.Ordinal);
            destination = Path.Combine(Path.GetDirectoryName(stage)!, name[1..marker]);
            File.WriteAllText(destination, "human backup");
        };

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Equal(pristine, File.ReadAllBytes(cursor.ConfigPath));
        Assert.NotNull(destination);
        Assert.Equal("human backup", File.ReadAllText(destination));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void BackupReplacementBeforeStatePublicationFailsClosed()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] pristine = File.ReadAllBytes(cursor.ConfigPath);
        string? backup = null;
        fixture.Service.TransitionHook = transition =>
        {
            if (transition != "before-state-publish")
            {
                return;
            }

            backup = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(cursor.ConfigPath)!,
                "*.bak.mcp-swap-dotnet-*"));
            File.Delete(backup);
            File.WriteAllText(backup, "human backup");
        };

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Equal(pristine, File.ReadAllBytes(cursor.ConfigPath));
        Assert.NotNull(backup);
        Assert.Equal("human backup", File.ReadAllText(backup));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void ConfigReplacementBeforeStatePublicationFailsClosed()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] human = "{\"human\":true}"u8.ToArray();
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-state-publish")
            {
                File.Delete(cursor.ConfigPath);
                File.WriteAllBytes(cursor.ConfigPath, human);
            }
        };

        SwapException failure = Assert.Throws<SwapException>(
            () => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Contains("recovery files retained", failure.Message, StringComparison.Ordinal);
        Assert.Equal(human, File.ReadAllBytes(cursor.ConfigPath));
        Assert.False(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void LateStateDestinationSurvivesWhileTheWholeUseRollsBack()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        byte[] pristine = File.ReadAllBytes(cursor.ConfigPath);
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-state-publish")
            {
                File.WriteAllText(fixture.StateFile, "human state");
            }
        };

        Assert.Throws<SwapException>(() => fixture.Service.Use(fixture.UseOptions(["cursor"])));

        Assert.Equal(pristine, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal("human state", File.ReadAllText(fixture.StateFile));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(cursor.ConfigPath)!,
            "*.bak.mcp-swap-dotnet-*"));
    }

    [Fact]
    public void LateBackupReplacementDuringRevertSurvivesAndTheSwapIsRestored()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        byte[] swapped = File.ReadAllBytes(cursor.ConfigPath);
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-backup-take-aside:cursor")
            {
                File.Delete(recovery.BackupPath);
                File.WriteAllText(recovery.BackupPath, "human backup");
            }
        };

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(swapped, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal("human backup", File.ReadAllText(recovery.BackupPath));
        Assert.True(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void ConfigReplacementBeforeRevertStatePublicationFailsClosed()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        byte[] human = "{\"human\":true}"u8.ToArray();
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-state-take-aside")
            {
                File.Delete(cursor.ConfigPath);
                File.WriteAllBytes(cursor.ConfigPath, human);
            }
        };

        SwapException failure = Assert.Throws<SwapException>(
            () => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Contains("recovery files retained", failure.Message, StringComparison.Ordinal);
        Assert.Equal(human, File.ReadAllBytes(cursor.ConfigPath));
        Assert.True(File.Exists(recovery.BackupPath));
        Assert.True(File.Exists(fixture.StateFile));
    }

    [Fact]
    public void BackupReplacementBeforeRevertStatePublicationFailsClosed()
    {
        using SwapFixture fixture = new();
        ClientInfo cursor = fixture.Catalog["cursor"];
        fixture.Seed(cursor, "{}");
        fixture.Service.Use(fixture.UseOptions(["cursor"]));
        RecoveryEntry recovery = LedgerCodec.Read(fixture.StateFile).Entries["cursor:user"];
        byte[] swapped = File.ReadAllBytes(cursor.ConfigPath);
        fixture.Service.TransitionHook = transition =>
        {
            if (transition == "before-state-take-aside")
            {
                File.WriteAllText(recovery.BackupPath, "human backup");
            }
        };

        Assert.Throws<SwapException>(() => fixture.Service.Revert(fixture.RevertOptions(["cursor"])));

        Assert.Equal(swapped, File.ReadAllBytes(cursor.ConfigPath));
        Assert.Equal("human backup", File.ReadAllText(recovery.BackupPath));
        Assert.True(File.Exists(fixture.StateFile));
    }

    private sealed class SwapFixture : IDisposable
    {
        internal SwapFixture()
        {
            Paths = new();
            Catalog = ClientCatalog.Create(Paths.Home, Paths.ConfigHome);
            Binary = CreateBinary("libtmux-mcp");
            string repository = Path.Combine(Paths.Root, "repo");
            string project = Path.Combine(repository, "src", "LibTmux.Mcp");
            Directory.CreateDirectory(project);
            File.WriteAllText(
                Path.Combine(project, "LibTmux.Mcp.csproj"),
                "<Project><PropertyGroup><AssemblyName>LibTmux.Mcp</AssemblyName><ToolCommandName>libtmux-mcp</ToolCommandName></PropertyGroup></Project>");
            Repository = repository;
            Preflight = new RecordingPreflight();
            Provisioner = new RecordingProvisioner();
            Runtime = new SwapRuntime(Paths.Home, Paths.ConfigHome, Paths.StateHome, "/opt/dotnet/dotnet");
            Service = new SwapService(
                Runtime,
                TextWriter.Null,
                TextWriter.Null,
                Provisioner,
                Preflight);
        }

        internal TestPaths Paths { get; }

        internal ClientCatalog Catalog { get; }

        internal SwapService Service { get; }

        internal RecordingPreflight Preflight { get; }

        internal RecordingProvisioner Provisioner { get; }

        internal SwapRuntime Runtime { get; }

        internal string Binary { get; }

        internal string Repository { get; }

        internal string StateFile => Path.Combine(
            Paths.StateHome,
            "libtmux-mcp-dev",
            "swap",
            "dotnet",
            "state.json");

        internal Dictionary<string, byte[]> SeedAllClients()
        {
            Dictionary<string, byte[]> pristine = new(StringComparer.Ordinal);
            foreach (ClientInfo client in Catalog.Clients)
            {
                string content = client.Format switch
                {
                    ConfigFormat.Json => "{\"theme\":\"dark\"}",
                    ConfigFormat.Jsonc => "{\n  // keep\n}\n",
                    ConfigFormat.Toml => "# keep\ntitle = \"dev\"\n",
                    _ => throw new InvalidOperationException(),
                };
                Seed(client, content);
                pristine[client.Name] = File.ReadAllBytes(client.ConfigPath);
            }

            return pristine;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822",
            Justification = "Fixture operations are intentionally instance-scoped at call sites.")]
        internal void Seed(ClientInfo client, string content) =>
            Seed(client, Encoding.UTF8.GetBytes(content));

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822",
            Justification = "Fixture operations are intentionally instance-scoped at call sites.")]
        internal void Seed(ClientInfo client, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(client.ConfigPath)!);
            File.WriteAllBytes(client.ConfigPath, content);
        }

        internal string CreateBinary(string name)
        {
            string path = Path.Combine(Paths.Root, "bin", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        internal CommandOptions UseOptions(
            IReadOnlyList<string>? clients = null,
            ConfigScope? scope = null,
            string? binary = null) => new()
            {
                Command = SwapCommand.Use,
                Source = SourceKind.Path,
                BinaryPath = binary ?? Binary,
                Repository = Repository,
                Clients = clients ?? Catalog.Clients.Select(client => client.Name).ToArray(),
                Scope = scope,
                NoPreflight = true,
            };

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822",
            Justification = "Fixture operations are intentionally instance-scoped at call sites.")]
        internal CommandOptions RevertOptions(
            IReadOnlyList<string>? clients = null,
            ConfigScope? scope = null) => new()
            {
                Command = SwapCommand.Revert,
                Clients = clients ?? [],
                Scope = scope,
            };

        internal ServerSpec? ReadServer(ClientInfo client, ConfigScope scope) =>
            ConfigCodec.Read(
                client,
                File.ReadAllBytes(client.ConfigPath),
                "tmux",
                Repository,
                client.Name == "claude" ? scope : ConfigScope.User);

        public void Dispose() => Paths.Dispose();
    }

    internal sealed class RecordingProvisioner : ISourceProvisioner
    {
        internal List<SourcePlan> Plans { get; } = [];

        internal Action<SourcePlan, CommandOptions>? OnPrepare { get; set; }

        public void Prepare(SourcePlan plan, CommandOptions options)
        {
            Plans.Add(plan);
            OnPrepare?.Invoke(plan, options);
        }
    }

    internal sealed class RecordingPreflight : IServerPreflight
    {
        internal List<ServerSpec> Specs { get; } = [];

        internal Func<ServerSpec, string?>? Failure { get; set; }

        public string? Probe(ServerSpec spec)
        {
            Specs.Add(spec);
            return Failure?.Invoke(spec);
        }
    }
}
