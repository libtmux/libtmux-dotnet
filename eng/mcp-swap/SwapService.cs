namespace LibTmux.McpSwap;

internal sealed class SwapException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class IncompleteRollbackException(string message, Exception inner) : IOException(message, inner);

internal sealed class SwapService
{
    private const string BackupSuffix = ".bak.mcp-swap-dotnet-";
    private static readonly HashSet<string> SafetyEnvironment = new(StringComparer.Ordinal)
    {
        "LIBTMUX_SAFETY",
    };
    private readonly SwapRuntime runtime;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly ISourceProvisioner provisioner;
    private readonly IServerPreflight preflight;
    private readonly ClientCatalog catalog;

    internal SwapService(
        SwapRuntime runtime,
        TextWriter output,
        TextWriter error,
        ISourceProvisioner provisioner,
        IServerPreflight preflight)
    {
        this.runtime = runtime;
        this.output = output;
        this.error = error;
        this.provisioner = provisioner;
        this.preflight = preflight;
        catalog = ClientCatalog.Create(runtime.Home, runtime.ConfigHome);
    }

    internal Action<string>? TransitionHook { get; set; }

    internal void Use(CommandOptions options)
    {
        try
        {
            if (options.Environment.ContainsKey("LIBTMUX_SAFETY"))
            {
                throw new InvalidDataException(
                    "LIBTMUX_SAFETY has been removed; select capabilities with LIBTMUX_TOOLSETS");
            }

            SourcePlan source = SourceResolver.Resolve(options, runtime.DotnetPath, runtime.StateHome);
            UseTransaction preview = PlanUse(options, source);
            ValidateAliases(preview.Plans, preview.Ledger, preview.OptionalStateArtifact, null);
            ReportNamingHint(source);
            if (options.DryRun)
            {
                PrintUse(preview.Plans, dryRun: true);
                return;
            }

            provisioner.Prepare(source, options);
            UseTransaction preflighted = PlanUse(options, source);
            ValidateAliases(
                preflighted.Plans,
                preflighted.Ledger,
                preflighted.OptionalStateArtifact,
                null);
            if (!options.NoPreflight)
            {
                Preflight(preflighted.Plans);
            }

            using SwapLock swapLock = SwapLock.Acquire(runtime);
            NativeFileSystem.CreatePrivateDirectory(runtime.StateDirectory);
            UseTransaction transaction = PlanUse(options, source);
            ValidateAliases(transaction.Plans, transaction.Ledger, transaction.StateArtifact, swapLock);
            if (!options.NoPreflight)
            {
                RequirePreflightStillCurrent(preflighted.Plans, transaction.Plans);
            }

            UsePlan[] changed = transaction.Plans.Where(plan => plan.Changed).ToArray();
            if (changed.Length == 0)
            {
                PrintUse(transaction.Plans, dryRun: false);
                return;
            }

            CommitUse(transaction, changed, swapLock);
            PrintUse(changed, dryRun: false);
        }
        catch (Exception failure) when (failure is not SwapException)
        {
            throw new SwapException(failure.Message, failure);
        }
    }

    internal void Revert(CommandOptions options)
    {
        try
        {
            RevertTransaction preview = PlanRevert(options);
            ValidateAliases(preview.Plans, preview.Ledger, preview.StateArtifact, null);
            if (options.DryRun)
            {
                PrintRevert(preview.Plans, dryRun: true);
                return;
            }

            if (preview.Plans.Count == 0)
            {
                return;
            }

            using SwapLock swapLock = SwapLock.Acquire(runtime);
            RevertTransaction transaction = PlanRevert(options);
            ValidateAliases(transaction.Plans, transaction.Ledger, transaction.StateArtifact, swapLock);
            CommitRevert(transaction, swapLock);
            PrintRevert(transaction.Plans, dryRun: false);
        }
        catch (Exception failure) when (failure is not SwapException)
        {
            throw new SwapException(failure.Message, failure);
        }
    }

    private UseTransaction PlanUse(CommandOptions options, SourcePlan source)
    {
        StateSnapshot state = LoadState();
        List<Target> targets = SelectUseTargets(options);
        List<UsePlan> plans = [];
        List<string> failures = [];
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        long sequence = state.Ledger.NextSequence;
        foreach (Target target in targets)
        {
            try
            {
                PathBinding config = NativeFileSystem.CaptureConfig(target.Client.ConfigPath);
                ValidateOutstandingLayers(state.Ledger, config);
                ServerSpec? current = ConfigCodec.Read(
                    target.Client,
                    config.File.Bytes,
                    source.ServerName,
                    source.Repository,
                    target.Scope);
                ServerSpec desired = source.Spec.MergeEnvironment(
                    current?.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal));
                ConfigEdit edit = ConfigCodec.Set(
                    target.Client,
                    config.File.Bytes,
                    source.ServerName,
                    desired,
                    source.Repository,
                    target.Scope,
                    options.Environment.ContainsKey("LIBTMUX_TOOLSETS")
                        ? SafetyEnvironment
                        : null);
                ServerSpec final = ConfigCodec.Read(
                    target.Client,
                    edit.Bytes,
                    source.ServerName,
                    source.Repository,
                    target.Scope)!;
                string key = LedgerCodec.StateKey(target.Client.Name, target.Scope);
                state.Ledger.Entries.TryGetValue(key, out RecoveryEntry? prior);
                if (prior is not null
                    && !string.Equals(prior.TargetPath, config.Physical, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{target.Label} recovery names another config target");
                }
                DestinationBinding? backup = null;
                if (edit.Action != ConfigAction.Unchanged)
                {
                    string backupPath = prior?.BackupPath ?? UniqueBackupPath(target, timestamp);
                    backup = NativeFileSystem.CaptureDestination(backupPath, required: prior is not null);
                    if (prior is null)
                    {
                        sequence++;
                    }
                }

                plans.Add(
                    new UsePlan(
                        target,
                        config,
                        backup,
                        edit.Bytes,
                        edit.Action,
                        final,
                        prior,
                        prior?.Sequence ?? sequence - 1,
                        prior?.SwappedAt ?? timestamp)
                    {
                        ServerName = source.ServerName,
                        Repository = source.Repository,
                    });
            }
            catch (Exception failure)
            {
                failures.Add($"[{target.Label}] {failure.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new SwapException(string.Join("; ", failures));
        }

        return new(plans, state.Ledger, state.Artifact, sequence);
    }

    private RevertTransaction PlanRevert(CommandOptions options)
    {
        StateSnapshot state = LoadState(required: true);
        HashSet<string> selected = SelectRecoveryKeys(options, state.Ledger);
        if (selected.Count == 0)
        {
            return new([], state.Ledger, state.Artifact!);
        }

        List<RevertPlan> plans = [];
        List<string> failures = [];
        foreach (IGrouping<string, string> group in selected.GroupBy(
            key => state.Ledger.Entries[key].TargetPath,
            StringComparer.Ordinal))
        {
            try
            {
                string[] ordered = group
                    .OrderByDescending(key => state.Ledger.Entries[key].Sequence)
                    .ToArray();
                RecoveryEntry newest = state.Ledger.Entries[ordered[0]];
                ClientInfo client = catalog[newest.Client];
                Target target = new(client, newest.Scope);
                PathBinding config = NativeFileSystem.CaptureConfig(client.ConfigPath);
                if (!string.Equals(config.Physical, group.Key, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{ordered[0]} physical config changed");
                }

                string[] allLayers = state.Ledger.Entries
                    .Where(pair => string.Equals(pair.Value.TargetPath, group.Key, StringComparison.Ordinal))
                    .OrderByDescending(pair => pair.Value.Sequence)
                    .Select(pair => pair.Key)
                    .ToArray();
                if (!ordered.SequenceEqual(allLayers.Take(ordered.Length), StringComparer.Ordinal))
                {
                    throw new InvalidDataException("cannot revert a recovery layer while a newer layer remains");
                }

                ValidateOutstandingLayers(state.Ledger, config);
                List<(string Key, RecoveryEntry Entry, DestinationBinding Backup)> entries = [];
                foreach (string key in ordered)
                {
                    RecoveryEntry entry = state.Ledger.Entries[key];
                    DestinationBinding backup = NativeFileSystem.CaptureDestination(entry.BackupPath, required: true);
                    if (!entry.BackupIdentity.Matches(backup.File!))
                    {
                        throw new InvalidDataException($"{key} backup identity changed");
                    }

                    entries.Add((key, entry, backup));
                }

                FileSnapshot oldest = entries[^1].Backup.File!;
                plans.Add(new(target, config, entries, oldest.Bytes, oldest.Identity.Permissions));
            }
            catch (Exception failure)
            {
                failures.Add($"[{group.Key}] {failure.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new SwapException(string.Join("; ", failures));
        }

        return new(plans, state.Ledger, state.Artifact!);
    }

    private void CommitUse(
        UseTransaction transaction,
        IReadOnlyList<UsePlan> changed,
        SwapLock swapLock)
    {
        Dictionary<string, FileSnapshot> owned = new(StringComparer.Ordinal);
        List<StagedUse> staged = [];
        string? stateStage = null;
        string? stateRecovery = null;
        try
        {
            RecoveryLedger next = Clone(transaction.Ledger);
            next.NextSequence = transaction.NextSequence;
            foreach (UsePlan plan in changed)
            {
                swapLock.Validate();
                string outputStage = StageOwned(
                    owned,
                    Path.GetDirectoryName(plan.Config.Physical)!,
                    Path.GetFileName(plan.Config.Logical),
                    "output",
                    plan.Output,
                    plan.Config.File.Identity.Permissions);
                string recovery = StageOwned(
                    owned,
                    Path.GetDirectoryName(plan.Config.Physical)!,
                    Path.GetFileName(plan.Config.Logical),
                    "recovery",
                    plan.Config.File.Bytes,
                    plan.Config.File.Identity.Permissions);
                string? backupStage = null;
                DestinationBinding backup = plan.Backup!;
                if (backup.File is null)
                {
                    backupStage = StageOwned(
                        owned,
                        backup.Parent.Physical,
                        Path.GetFileName(backup.Logical),
                        "backup",
                        plan.Config.File.Bytes,
                        plan.Config.File.Identity.Permissions);
                }

                FileSnapshot outputSnapshot = owned[outputStage];
                FileSnapshot backupSnapshot = backup.File ?? owned[backupStage!];
                foreach (RecoveryEntry existing in next.Entries.Values.Where(
                    entry => string.Equals(entry.TargetPath, plan.Config.Physical, StringComparison.Ordinal)))
                {
                    existing.ExpectedConfig = StoredIdentity.From(outputSnapshot);
                }

                string key = LedgerCodec.StateKey(plan.Target.Client.Name, plan.Target.Scope);
                next.Entries[key] = new()
                {
                    Client = plan.Target.Client.Name,
                    Scope = plan.Target.Scope,
                    Sequence = plan.Sequence,
                    Server = plan.ServerName,
                    Action = plan.Action,
                    Repository = plan.Repository,
                    SwappedAt = plan.SwappedAt,
                    ConfigPath = plan.Config.Logical,
                    TargetPath = plan.Config.Physical,
                    ConfigParent = StoredDirectory.From(plan.Config.Parent),
                    LinkTarget = plan.Config.LinkTarget,
                    LinkIdentity = plan.Config.LinkIdentity is null
                        ? null
                        : IdentityWithoutDigest(plan.Config.LinkIdentity),
                    BackupPath = backup.Logical,
                    BackupIdentity = StoredIdentity.From(backupSnapshot),
                    ExpectedConfig = StoredIdentity.From(outputSnapshot),
                    Route = StoredSpec.From(plan.Spec),
                };
                staged.Add(new(plan, outputStage, recovery, backupStage));
            }

            byte[] stateBytes = LedgerCodec.Encode(next);
            stateStage = StageOwned(
                owned,
                transaction.StateArtifact.Parent.Physical,
                Path.GetFileName(runtime.StateFile),
                "state",
                stateBytes,
                0x180);
            if (transaction.StateArtifact.File is not null)
            {
                stateRecovery = StageOwned(
                    owned,
                    transaction.StateArtifact.Parent.Physical,
                    Path.GetFileName(runtime.StateFile),
                    "recovery-state",
                    transaction.StateArtifact.File.Bytes,
                    transaction.StateArtifact.File.Identity.Permissions);
            }

            ApplyUse(staged, transaction.StateArtifact, stateStage, stateRecovery, owned, swapLock);
        }
        catch (Exception failure)
        {
            if (failure is not IncompleteRollbackException)
            {
                CleanupOwned(owned);
            }

            throw new SwapException($"swap failed: {failure.Message}", failure);
        }
    }

    private void ApplyUse(
        IReadOnlyList<StagedUse> staged,
        DestinationBinding stateArtifact,
        string stateStage,
        string? stateRecovery,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock)
    {
        List<ConfigCommit> configs = [];
        List<ArtifactCommit> backups = [];
        FileSnapshot? committedState = null;
        bool stateTaken = false;
        try
        {
            ValidateAll(staged.Select(item => item.Plan), stateArtifact, swapLock);
            foreach (StagedUse item in staged)
            {
                TransitionHook?.Invoke($"before-config:{item.Plan.Target.Client.Name}");
                TransitionHook?.Invoke($"before-config-take-aside:{item.Plan.Target.Client.Name}");
                swapLock.Validate();
                NativeFileSystem.Validate(item.Plan.Config);
                FileSnapshot retained = NativeFileSystem.TakeAside(
                    item.Plan.Config.Physical,
                    item.Recovery,
                    item.Plan.Config.File,
                    owned[item.Recovery]);
                owned[item.Recovery] = retained;
                configs.Add(new(item.Plan.Config, item.Recovery, null));
                TransitionHook?.Invoke($"before-config-publish:{item.Plan.Target.Client.Name}");
                FileSnapshot committed = NativeFileSystem.PublishNoReplace(
                    item.Output,
                    item.Plan.Config.Physical,
                    owned[item.Output]);
                owned.Remove(item.Output);
                configs[^1] = configs[^1] with { Committed = committed };
                TransitionHook?.Invoke($"after-config-publish:{item.Plan.Target.Client.Name}");
                ValidateCommittedConfig(item.Plan, committed);
            }

            foreach (StagedUse item in staged.Where(item => item.BackupStage is not null))
            {
                DestinationBinding backup = item.Plan.Backup!;
                TransitionHook?.Invoke($"before-backup-publish:{item.Plan.Target.Client.Name}");
                NativeFileSystem.Validate(backup);
                FileSnapshot committed = NativeFileSystem.PublishNoReplace(
                    item.BackupStage!,
                    backup.Physical,
                    owned[item.BackupStage!]);
                owned.Remove(item.BackupStage!);
                backups.Add(new(backup, committed));
            }

            swapLock.Validate();
            ValidateCommittedUseArtifacts(configs, backups);
            NativeFileSystem.Validate(stateArtifact);
            if (stateArtifact.File is not null)
            {
                TransitionHook?.Invoke("before-state-take-aside");
                FileSnapshot retained = NativeFileSystem.TakeAside(
                    stateArtifact.Physical,
                    stateRecovery!,
                    stateArtifact.File,
                    owned[stateRecovery!]);
                owned[stateRecovery!] = retained;
                stateTaken = true;
            }

            TransitionHook?.Invoke("before-state-publish");
            swapLock.Validate();
            ValidateCommittedUseArtifacts(configs, backups);
            committedState = NativeFileSystem.PublishNoReplace(
                stateStage,
                stateArtifact.Physical,
                owned[stateStage]);
            owned.Remove(stateStage);
            _ = LedgerCodec.Read(runtime.StateFile);
            ValidateCommittedUseArtifacts(configs, backups);
        }
        catch (Exception failure)
        {
            List<string> rollback = RollbackUse(
                configs,
                backups,
                stateArtifact,
                committedState,
                stateTaken,
                stateRecovery,
                owned,
                swapLock);
            if (rollback.Count > 0)
            {
                string retained = string.Join(", ", owned.Keys.Order(StringComparer.Ordinal));
                throw new IncompleteRollbackException(
                    failure.Message
                    + $"; rollback incomplete: {string.Join("; ", rollback)}"
                    + $"; recovery files retained: {retained}",
                    failure);
            }

            throw new IOException(failure.Message, failure);
        }

        CleanupOwnedOrThrow(owned, swapLock, "swap");
    }

    private static void ValidateCommittedUseArtifacts(
        IEnumerable<ConfigCommit> configs,
        IEnumerable<ArtifactCommit> backups)
    {
        foreach (ConfigCommit config in configs)
        {
            ValidateCommittedBinding(
                config.Binding,
                config.Committed ?? throw new IOException("committed config identity is missing"),
                config.Binding.Logical);
        }

        foreach (ArtifactCommit backup in backups)
        {
            NativeFileSystem.Validate(backup.Binding with { File = backup.Committed });
        }
    }

    private void CommitRevert(RevertTransaction transaction, SwapLock swapLock)
    {
        Dictionary<string, FileSnapshot> owned = new(StringComparer.Ordinal);
        List<StagedRevert> staged = [];
        string? stateStage = null;
        string stateRecovery;
        try
        {
            RecoveryLedger next = Clone(transaction.Ledger);
            foreach (RevertPlan plan in transaction.Plans)
            {
                string outputStage = StageOwned(
                    owned,
                    Path.GetDirectoryName(plan.Config.Physical)!,
                    Path.GetFileName(plan.Config.Logical),
                    "restore",
                    plan.Output,
                    plan.OutputMode);
                string configRecovery = StageOwned(
                    owned,
                    Path.GetDirectoryName(plan.Config.Physical)!,
                    Path.GetFileName(plan.Config.Logical),
                    "recovery",
                    plan.Config.File.Bytes,
                    plan.Config.File.Identity.Permissions);
                List<string> backupRecoveries = [];
                foreach ((string key, RecoveryEntry _, DestinationBinding backup) in plan.Entries)
                {
                    string recovery = StageOwned(
                        owned,
                        backup.Parent.Physical,
                        Path.GetFileName(backup.Logical),
                        "recovery-backup",
                        backup.File!.Bytes,
                        backup.File.Identity.Permissions);
                    backupRecoveries.Add(recovery);
                    next.Entries.Remove(key);
                }

                FileSnapshot outputSnapshot = owned[outputStage];
                foreach (RecoveryEntry remaining in next.Entries.Values.Where(
                    entry => string.Equals(entry.TargetPath, plan.Config.Physical, StringComparison.Ordinal)))
                {
                    remaining.ExpectedConfig = StoredIdentity.From(outputSnapshot);
                    ClientInfo client = catalog[remaining.Client];
                    ServerSpec? actual = ConfigCodec.Read(
                        client,
                        outputSnapshot.Bytes,
                        remaining.Server,
                        remaining.Repository,
                        remaining.Scope);
                    if (!remaining.Route.ToSpec().Equals(actual))
                    {
                        throw new InvalidDataException(
                            $"backup does not restore remaining route {LedgerCodec.StateKey(remaining.Client, remaining.Scope)}");
                    }
                }

                staged.Add(new(plan, outputStage, configRecovery, backupRecoveries));
            }

            if (next.Entries.Count > 0)
            {
                stateStage = StageOwned(
                    owned,
                    transaction.StateArtifact.Parent.Physical,
                    Path.GetFileName(runtime.StateFile),
                    "state",
                    LedgerCodec.Encode(next),
                    0x180);
            }

            stateRecovery = StageOwned(
                owned,
                transaction.StateArtifact.Parent.Physical,
                Path.GetFileName(runtime.StateFile),
                "recovery-state",
                transaction.StateArtifact.File!.Bytes,
                transaction.StateArtifact.File.Identity.Permissions);
            ApplyRevert(staged, transaction.StateArtifact, stateStage, stateRecovery, owned, swapLock);
        }
        catch (Exception failure)
        {
            if (failure is not IncompleteRollbackException)
            {
                CleanupOwned(owned);
            }

            throw new SwapException($"revert failed: {failure.Message}", failure);
        }
    }

    private void ApplyRevert(
        IReadOnlyList<StagedRevert> staged,
        DestinationBinding stateArtifact,
        string? stateStage,
        string stateRecovery,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock)
    {
        List<ConfigCommit> configs = [];
        List<BackupTakeAside> backups = [];
        FileSnapshot? committedState = null;
        bool stateTaken = false;
        try
        {
            ValidateAll(staged.Select(item => item.Plan), stateArtifact, swapLock);
            foreach (StagedRevert item in staged)
            {
                TransitionHook?.Invoke($"before-config:{item.Plan.Target.Client.Name}");
                TransitionHook?.Invoke($"before-config-take-aside:{item.Plan.Target.Client.Name}");
                NativeFileSystem.Validate(item.Plan.Config);
                FileSnapshot retained = NativeFileSystem.TakeAside(
                    item.Plan.Config.Physical,
                    item.ConfigRecovery,
                    item.Plan.Config.File,
                    owned[item.ConfigRecovery]);
                owned[item.ConfigRecovery] = retained;
                configs.Add(new(item.Plan.Config, item.ConfigRecovery, null));
                TransitionHook?.Invoke($"before-config-publish:{item.Plan.Target.Client.Name}");
                FileSnapshot committed = NativeFileSystem.PublishNoReplace(
                    item.Output,
                    item.Plan.Config.Physical,
                    owned[item.Output]);
                owned.Remove(item.Output);
                configs[^1] = configs[^1] with { Committed = committed };
                TransitionHook?.Invoke($"after-config-publish:{item.Plan.Target.Client.Name}");
                ValidateCommittedBinding(item.Plan.Config, committed, item.Plan.Target.Label);
            }

            foreach (StagedRevert item in staged)
            {
                for (int index = 0; index < item.Plan.Entries.Count; index++)
                {
                    DestinationBinding backup = item.Plan.Entries[index].Backup;
                    string recovery = item.BackupRecoveries[index];
                    TransitionHook?.Invoke($"before-backup-take-aside:{item.Plan.Target.Client.Name}");
                    NativeFileSystem.Validate(backup);
                    FileSnapshot retained = NativeFileSystem.TakeAside(
                        backup.Physical,
                        recovery,
                        backup.File!,
                        owned[recovery]);
                    owned[recovery] = retained;
                    backups.Add(new(backup, recovery));
                }
            }

            ValidateCommittedRevertArtifacts(configs, backups);
            NativeFileSystem.Validate(stateArtifact);
            TransitionHook?.Invoke("before-state-take-aside");
            swapLock.Validate();
            ValidateCommittedRevertArtifacts(configs, backups);
            NativeFileSystem.Validate(stateArtifact);
            FileSnapshot retainedState = NativeFileSystem.TakeAside(
                stateArtifact.Physical,
                stateRecovery,
                stateArtifact.File!,
                owned[stateRecovery]);
            owned[stateRecovery] = retainedState;
            stateTaken = true;
            if (stateStage is not null)
            {
                TransitionHook?.Invoke("before-state-publish");
                committedState = NativeFileSystem.PublishNoReplace(
                    stateStage,
                    stateArtifact.Physical,
                    owned[stateStage]);
                owned.Remove(stateStage);
                _ = LedgerCodec.Read(runtime.StateFile);
            }
            else if (NativeFileSystem.TryLStat(runtime.StateFile, out _))
            {
                throw new IOException("empty recovery state was not removed");
            }

            ValidateCommittedRevertArtifacts(configs, backups);
            NativeFileSystem.Validate(stateArtifact with { File = committedState });
        }
        catch (Exception failure)
        {
            List<string> rollback = RollbackRevert(
                configs,
                backups,
                stateArtifact,
                committedState,
                stateTaken,
                stateRecovery,
                owned,
                swapLock);
            if (rollback.Count > 0)
            {
                string retained = string.Join(", ", owned.Keys.Order(StringComparer.Ordinal));
                throw new IncompleteRollbackException(
                    failure.Message
                    + $"; rollback incomplete: {string.Join("; ", rollback)}"
                    + $"; recovery files retained: {retained}",
                    failure);
            }

            throw new IOException(failure.Message, failure);
        }

        CleanupOwnedOrThrow(owned, swapLock, "revert");
    }

    private static void ValidateCommittedRevertArtifacts(
        IEnumerable<ConfigCommit> configs,
        IEnumerable<BackupTakeAside> backups)
    {
        foreach (ConfigCommit config in configs)
        {
            ValidateCommittedBinding(
                config.Binding,
                config.Committed ?? throw new IOException("restored config identity is missing"),
                config.Binding.Logical);
        }

        foreach (BackupTakeAside backup in backups)
        {
            NativeFileSystem.Validate(backup.Binding with { File = null });
        }
    }

    private static List<string> RollbackUse(
        IReadOnlyList<ConfigCommit> configs,
        IReadOnlyList<ArtifactCommit> backups,
        DestinationBinding stateArtifact,
        FileSnapshot? committedState,
        bool stateTaken,
        string? stateRecovery,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock)
    {
        List<string> failures = [];
        RestoreState(stateArtifact, committedState, stateTaken, stateRecovery, owned, swapLock, failures);
        foreach (ArtifactCommit backup in backups.Reverse())
        {
            TryRollback(
                failures,
                $"backup {backup.Binding.Logical}",
                () => NativeFileSystem.RemoveExact(backup.Binding.Physical, backup.Committed));
        }

        foreach (ConfigCommit config in configs.Reverse())
        {
            RestoreConfig(config, owned, swapLock, failures);
        }

        return failures;
    }

    private static List<string> RollbackRevert(
        IReadOnlyList<ConfigCommit> configs,
        IReadOnlyList<BackupTakeAside> backups,
        DestinationBinding stateArtifact,
        FileSnapshot? committedState,
        bool stateTaken,
        string stateRecovery,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock)
    {
        List<string> failures = [];
        RestoreState(stateArtifact, committedState, stateTaken, stateRecovery, owned, swapLock, failures);
        foreach (BackupTakeAside backup in backups.Reverse())
        {
            TryRollback(failures, $"backup {backup.Binding.Logical}", () =>
            {
                if (NativeFileSystem.TryLStat(backup.Binding.Physical, out _))
                {
                    throw new IOException("backup destination appeared during rollback");
                }

                _ = NativeFileSystem.PublishNoReplace(
                    backup.Recovery,
                    backup.Binding.Physical,
                    owned[backup.Recovery]);
                owned.Remove(backup.Recovery);
            });
        }

        foreach (ConfigCommit config in configs.Reverse())
        {
            RestoreConfig(config, owned, swapLock, failures);
        }

        return failures;
    }

    private static void RestoreState(
        DestinationBinding stateArtifact,
        FileSnapshot? committedState,
        bool stateTaken,
        string? stateRecovery,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock,
        ICollection<string> failures)
    {
        TryRollback(failures, "swap state", () =>
        {
            swapLock.Validate();
            if (committedState is not null)
            {
                NativeFileSystem.RemoveExact(stateArtifact.Physical, committedState);
            }

            if (stateTaken)
            {
                _ = NativeFileSystem.PublishNoReplace(
                    stateRecovery!,
                    stateArtifact.Physical,
                    owned[stateRecovery!]);
                owned.Remove(stateRecovery!);
            }
        });
    }

    private static void RestoreConfig(
        ConfigCommit config,
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock,
        ICollection<string> failures)
    {
        TryRollback(failures, $"config {config.Binding.Logical}", () =>
        {
            swapLock.Validate();
            if (config.Committed is not null)
            {
                NativeFileSystem.RemoveExact(config.Binding.Physical, config.Committed);
            }
            else if (NativeFileSystem.TryLStat(config.Binding.Physical, out _))
            {
                throw new IOException("unowned config appeared during rollback");
            }

            _ = NativeFileSystem.PublishNoReplace(
                config.Recovery,
                config.Binding.Physical,
                owned[config.Recovery]);
            owned.Remove(config.Recovery);
            NativeFileSystem.Validate(config.Binding);
        });
    }

    private static void TryRollback(ICollection<string> failures, string subject, Action action)
    {
        try
        {
            action();
        }
        catch (Exception failure)
        {
            failures.Add($"{subject}: {failure.Message}");
        }
    }

    private void ValidateOutstandingLayers(RecoveryLedger ledger, PathBinding config)
    {
        foreach ((string key, RecoveryEntry entry) in ledger.Entries.Where(
            pair => string.Equals(pair.Value.TargetPath, config.Physical, StringComparison.Ordinal)))
        {
            if (!string.Equals(entry.ConfigPath, config.Logical, StringComparison.Ordinal)
                || !string.Equals(entry.TargetPath, config.Physical, StringComparison.Ordinal)
                || !MatchesDirectory(entry.ConfigParent, config.Parent)
                || !string.Equals(entry.LinkTarget, config.LinkTarget, StringComparison.Ordinal)
                || !MatchesLink(entry.LinkIdentity, config.LinkIdentity)
                || !entry.ExpectedConfig.Matches(config.File))
            {
                throw new InvalidDataException($"{key} config identity changed");
            }

            DestinationBinding backup = NativeFileSystem.CaptureDestination(entry.BackupPath, required: true);
            if (!entry.BackupIdentity.Matches(backup.File!))
            {
                throw new InvalidDataException($"{key} backup identity changed");
            }

            ClientInfo client = catalog[entry.Client];
            ServerSpec? actual = ConfigCodec.Read(
                client,
                config.File.Bytes,
                entry.Server,
                entry.Repository,
                entry.Scope);
            if (!entry.Route.ToSpec().Equals(actual))
            {
                throw new InvalidDataException($"{key} server route changed");
            }
        }
    }

    private static bool MatchesLink(StoredIdentity? stored, NativeIdentity? current) =>
        stored is null ? current is null : current is not null && stored.Device == current.Device
            && stored.Inode == current.Inode
            && stored.Mode == current.Mode
            && stored.LinkCount == current.LinkCount
            && stored.UserId == current.UserId
            && stored.Size == current.Size
            && stored.ModifiedNanoseconds == current.ModifiedNanoseconds;

    private static bool MatchesDirectory(StoredDirectory stored, DirectoryBinding current) =>
        string.Equals(stored.Logical, current.Logical, StringComparison.Ordinal)
        && string.Equals(stored.Physical, current.Physical, StringComparison.Ordinal)
        && string.Equals(stored.LinkTarget, current.LinkTarget, StringComparison.Ordinal)
        && stored.LinkIdentity == current.LinkIdentity
        && stored.Identity.Device == current.Identity.Device
        && stored.Identity.Inode == current.Identity.Inode
        && stored.Identity.Mode == current.Identity.Mode
        && stored.Identity.UserId == current.Identity.UserId;

    private List<Target> SelectUseTargets(CommandOptions options)
    {
        IReadOnlyList<ClientInfo> clients = options.Clients.Count > 0
            ? catalog.Select(options.Clients)
            : catalog.Clients.Where(client => IsPresent(client)).ToArray();
        if (clients.Count == 0)
        {
            throw new SwapException("no CLIs detected — nothing to do");
        }

        return clients.Select(client => new Target(
            client,
            client.Name == "claude" ? options.Scope ?? ConfigScope.Project : ConfigScope.User)).ToList();
    }

    private HashSet<string> SelectRecoveryKeys(CommandOptions options, RecoveryLedger ledger)
    {
        string[] clients = options.Clients.Count > 0
            ? catalog.Select(options.Clients).Select(client => client.Name).ToArray()
            : ledger.Entries.Values.Select(entry => entry.Client).Distinct(StringComparer.Ordinal).ToArray();
        if (clients.Length == 0)
        {
            throw new SwapException("no recorded swaps — nothing to revert");
        }

        HashSet<string> selected = new(StringComparer.Ordinal);
        foreach (string client in clients)
        {
            IEnumerable<ConfigScope> scopes = client == "claude" && options.Scope is null
                ? [ConfigScope.User, ConfigScope.Project]
                : [client == "claude" ? options.Scope ?? ConfigScope.Project : ConfigScope.User];
            bool matched = false;
            foreach (ConfigScope scope in scopes)
            {
                string key = LedgerCodec.StateKey(client, scope);
                if (ledger.Entries.ContainsKey(key))
                {
                    selected.Add(key);
                    matched = true;
                }
            }

            if (!matched)
            {
                output.WriteLine($"[{client}] no state entry — skip");
            }
        }

        return selected;
    }

    private StateSnapshot LoadState(bool required = false)
    {
        string? parent = Path.GetDirectoryName(runtime.StateFile);
        if (parent is null || !Directory.Exists(parent))
        {
            if (required)
            {
                throw new SwapException("no recorded swaps — nothing to revert");
            }

            return new(new(), null);
        }

        DestinationBinding artifact = NativeFileSystem.CaptureDestination(runtime.StateFile);
        if (artifact.File is null)
        {
            if (required)
            {
                throw new SwapException("no recorded swaps — nothing to revert");
            }

            return new(new(), artifact);
        }

        LedgerCodec.ValidatePrivate(artifact.File, "recovery state");
        return new(LedgerCodec.Decode(artifact.File.Bytes), artifact);
    }

    private void Preflight(IReadOnlyList<UsePlan> plans)
    {
        List<ServerSpec> distinct = DistinctSpecs(plans);
        List<string> failures = [];
        foreach (ServerSpec spec in distinct)
        {
            error.WriteLine($"preflight: {spec.Command} {string.Join(' ', spec.Arguments)}");
            string? failure = preflight.Probe(spec);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new SwapException("preflight failed, nothing written: " + string.Join("; ", failures));
        }
    }

    private static void RequirePreflightStillCurrent(
        IReadOnlyList<UsePlan> preflighted,
        IReadOnlyList<UsePlan> current)
    {
        List<ServerSpec> expected = DistinctSpecs(preflighted);
        List<ServerSpec> actual = DistinctSpecs(current);
        if (expected.Count != actual.Count
            || expected.Any(spec => !actual.Any(candidate => candidate.Equals(spec))))
        {
            throw new SwapException("configuration changed after preflight; retry the swap");
        }
    }

    private static List<ServerSpec> DistinctSpecs(IEnumerable<UsePlan> plans)
    {
        List<ServerSpec> distinct = [];
        foreach (ServerSpec spec in plans.Select(plan => plan.Spec))
        {
            if (!distinct.Any(existing => existing.Equals(spec)))
            {
                distinct.Add(spec);
            }
        }

        return distinct;
    }

    private void ReportNamingHint(SourcePlan source)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        bool serverPointsHere = false;
        foreach (ClientInfo client in catalog.Clients.Where(client => File.Exists(client.ConfigPath)))
        {
            try
            {
                IReadOnlyDictionary<string, ServerSpec> entries = ConfigCodec.ReadAll(
                    client,
                    File.ReadAllBytes(client.ConfigPath),
                    source.Repository);
                foreach ((string name, ServerSpec spec) in entries)
                {
                    string? local = LocalRepository(spec);
                    if (local is null
                        || !string.Equals(
                            Path.GetFullPath(local),
                            source.Repository,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(name, source.ServerName, StringComparison.Ordinal))
                    {
                        serverPointsHere = true;
                    }
                    else
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException)
            {
            }
        }

        if (!serverPointsHere && names.Count > 0)
        {
            error.WriteLine(
                $"note: nothing is registered under server '{source.ServerName}', but this repo is registered as [{string.Join(", ", names)}] — pass --server {names.Min} to target it");
        }
    }

    private static string? LocalRepository(ServerSpec spec)
    {
        if (Path.GetFileName(spec.Command) is "dotnet" or "dotnet.exe")
        {
            int project = spec.Arguments.ToList().IndexOf("--project");
            if (project >= 0 && project + 1 < spec.Arguments.Count)
            {
                string? parent = Path.GetDirectoryName(spec.Arguments[project + 1]);
                return parent is null ? null : Path.GetFullPath(Path.Combine(parent, "..", ".."));
            }
        }

        string normalized = spec.Command.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        string marker = $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}";
        int index = normalized.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? normalized[..index] : null;
    }

    private void ValidateAliases(
        IEnumerable<ITransactionPlan> plans,
        RecoveryLedger ledger,
        DestinationBinding? stateArtifact,
        SwapLock? swapLock)
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        Dictionary<(ulong Device, ulong Inode), string> inodes = [];
        Dictionary<string, string> configPaths = new(StringComparer.Ordinal);
        Dictionary<(ulong Device, ulong Inode), string> configInodes = [];
        List<ITransactionPlan> values = plans.ToList();
        HashSet<string> plannedConfigTargets = values
            .Select(plan => plan.Config.Physical)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> plannedBackups = values
            .SelectMany(plan => plan.Backups)
            .Select(backup => backup.Logical)
            .ToHashSet(StringComparer.Ordinal);
        if (swapLock is not null)
        {
            Claim(swapLock.Logical, swapLock.Physical, swapLock.Identity, "swap lock", false);
        }
        else
        {
            LockObservation observation = SwapLock.Inspect(runtime);
            Claim(
                observation.Logical,
                observation.Physical,
                observation.Identity,
                "swap lock",
                false);
        }

        foreach (ITransactionPlan plan in values)
        {
            Claim(
                plan.Config.Logical,
                plan.Config.Physical,
                plan.Config.File.Identity,
                $"{plan.Target.Label} config",
                true);
        }

        foreach (RecoveryEntry entry in ledger.Entries.Values
            .GroupBy(value => value.TargetPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(entry => !plannedConfigTargets.Contains(entry.TargetPath)))
        {
            Claim(
                entry.ConfigPath,
                entry.TargetPath,
                ToNative(entry.ExpectedConfig),
                $"{LedgerCodec.StateKey(entry.Client, entry.Scope)} recovery target",
                true);
        }

        foreach (ITransactionPlan plan in values)
        {
            foreach (DestinationBinding backup in plan.Backups)
            {
                Claim(
                    backup.Logical,
                    backup.Physical,
                    backup.File?.Identity,
                    $"{plan.Target.Label} backup",
                    false);
            }
        }

        foreach (RecoveryEntry entry in ledger.Entries.Values.Where(
            entry => !plannedBackups.Contains(entry.BackupPath)))
        {
            Claim(
                entry.BackupPath,
                NativeFileSystem.ProspectivePhysical(entry.BackupPath),
                ToNative(entry.BackupIdentity),
                $"{LedgerCodec.StateKey(entry.Client, entry.Scope)} recovery backup",
                false);
        }

        string stateLogical = stateArtifact?.Logical ?? runtime.StateFile;
        string statePhysical = stateArtifact?.Physical ?? NativeFileSystem.ProspectivePhysical(runtime.StateFile);
        Claim(stateLogical, statePhysical, stateArtifact?.File?.Identity, "swap state", false);

        void Claim(
            string logical,
            string physical,
            NativeIdentity? identity,
            string owner,
            bool config)
        {
            if (config)
            {
                if (configPaths.TryGetValue(physical, out string? duplicate)
                    || (identity is not null
                        && configInodes.TryGetValue((identity.Device, identity.Inode), out duplicate)))
                {
                    throw new SwapException($"duplicate physical config target for {duplicate} and {owner}");
                }

                configPaths[physical] = owner;
                if (identity is not null)
                {
                    configInodes[(identity.Device, identity.Inode)] = owner;
                }
            }

            foreach (string path in new[] { Path.GetFullPath(logical), Path.GetFullPath(physical) }.Distinct(StringComparer.Ordinal))
            {
                if (paths.TryGetValue(path, out string? duplicate) && duplicate != owner)
                {
                    throw new SwapException($"duplicate transaction destination for {duplicate} and {owner}");
                }

                paths[path] = owner;
            }

            if (identity is not null)
            {
                (ulong Device, ulong Inode) key = (identity.Device, identity.Inode);
                if (inodes.TryGetValue(key, out string? duplicate) && duplicate != owner)
                {
                    throw new SwapException($"duplicate transaction destination for {duplicate} and {owner}");
                }

                inodes[key] = owner;
            }
        }
    }

    private static NativeIdentity ToNative(StoredIdentity identity) => new(
        identity.Device,
        identity.Inode,
        identity.Mode,
        identity.LinkCount,
        identity.UserId,
        identity.Size,
        identity.ModifiedNanoseconds);

    private static void ValidateAll(
        IEnumerable<ITransactionPlan> plans,
        DestinationBinding stateArtifact,
        SwapLock swapLock)
    {
        swapLock.Validate();
        foreach (ITransactionPlan plan in plans)
        {
            NativeFileSystem.Validate(plan.Config);
            foreach (DestinationBinding backup in plan.Backups)
            {
                NativeFileSystem.Validate(backup);
            }
        }

        NativeFileSystem.Validate(stateArtifact);
        swapLock.Validate();
    }

    private static void ValidateCommittedConfig(UsePlan plan, FileSnapshot committed)
    {
        PathBinding current = NativeFileSystem.CaptureConfig(plan.Config.Logical);
        ValidateCommittedBinding(plan.Config, current, committed, plan.Target.Label);

        ServerSpec? actual = ConfigCodec.Read(
            plan.Target.Client,
            current.File.Bytes,
            plan.ServerName,
            plan.Repository,
            plan.Target.Scope);
        if (!plan.Spec.Equals(actual))
        {
            throw new IOException($"{plan.Target.Label} committed route is not exact");
        }
    }

    private static void ValidateCommittedBinding(
        PathBinding expected,
        FileSnapshot committed,
        string label)
    {
        PathBinding current = NativeFileSystem.CaptureConfig(expected.Logical);
        ValidateCommittedBinding(expected, current, committed, label);
    }

    private static void ValidateCommittedBinding(
        PathBinding expected,
        PathBinding current,
        FileSnapshot committed,
        string label)
    {
        if (!NativeFileSystem.SameDirectory(current.Parent, expected.Parent)
            || !string.Equals(current.Physical, expected.Physical, StringComparison.Ordinal)
            || !string.Equals(current.LinkTarget, expected.LinkTarget, StringComparison.Ordinal)
            || current.LinkIdentity != expected.LinkIdentity
            || !current.File.SameFile(committed))
        {
            throw new IOException($"{label} committed config is not exact");
        }
    }

    private static string UniqueBackupPath(Target target, string timestamp)
    {
        string suffix = BackupSuffix + timestamp;
        if (target.Client.Name == "claude")
        {
            suffix += "-" + target.Scope.ToString().ToLowerInvariant();
        }

        string candidate = target.Client.ConfigPath + suffix;
        for (int attempt = 1; NativeFileSystem.TryLStat(candidate, out _); attempt++)
        {
            candidate = target.Client.ConfigPath + suffix + $"-{attempt}";
        }

        return candidate;
    }

    private static RecoveryLedger Clone(RecoveryLedger ledger)
    {
        RecoveryLedger clone = new() { NextSequence = ledger.NextSequence };
        foreach ((string key, RecoveryEntry entry) in ledger.Entries)
        {
            clone.Entries[key] = new()
            {
                Client = entry.Client,
                Scope = entry.Scope,
                Sequence = entry.Sequence,
                Server = entry.Server,
                Action = entry.Action,
                Repository = entry.Repository,
                SwappedAt = entry.SwappedAt,
                ConfigPath = entry.ConfigPath,
                TargetPath = entry.TargetPath,
                ConfigParent = entry.ConfigParent,
                LinkTarget = entry.LinkTarget,
                LinkIdentity = entry.LinkIdentity,
                BackupPath = entry.BackupPath,
                BackupIdentity = entry.BackupIdentity,
                ExpectedConfig = entry.ExpectedConfig,
                Route = entry.Route,
            };
        }

        return clone;
    }

    private static StoredIdentity IdentityWithoutDigest(NativeIdentity identity) => new(
        identity.Device,
        identity.Inode,
        identity.Mode,
        identity.LinkCount,
        identity.UserId,
        identity.Size,
        identity.ModifiedNanoseconds,
        string.Empty);

    private static string StageOwned(
        Dictionary<string, FileSnapshot> owned,
        string directory,
        string logicalName,
        string role,
        byte[] bytes,
        int mode)
    {
        string stage = NativeFileSystem.Stage(directory, logicalName, role, bytes, mode);
        owned[stage] = NativeFileSystem.ReadStable(stage, Math.Max(bytes.Length, 1) + 1);
        return stage;
    }

    private static void CleanupOwned(Dictionary<string, FileSnapshot> owned)
    {
        foreach ((string path, FileSnapshot snapshot) in owned.ToArray())
        {
            try
            {
                if (NativeFileSystem.TryLStat(path, out _))
                {
                    NativeFileSystem.RemoveExact(path, snapshot);
                }

                owned.Remove(path);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void CleanupOwnedOrThrow(
        Dictionary<string, FileSnapshot> owned,
        SwapLock swapLock,
        string operation)
    {
        List<string> failures = [];
        foreach ((string path, FileSnapshot snapshot) in owned.ToArray())
        {
            try
            {
                swapLock.Validate();
                if (NativeFileSystem.TryLStat(path, out _))
                {
                    NativeFileSystem.RemoveExact(path, snapshot);
                }

                owned.Remove(path);
            }
            catch (Exception failure)
            {
                failures.Add($"{path}: {failure.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new SwapException($"{operation} cleanup failed: {string.Join("; ", failures)}");
        }
    }

    private static bool IsPresent(ClientInfo client) =>
        File.Exists(client.ConfigPath)
        && ExecutableFinder.Find(client.Binary) is not null;

    private void PrintUse(IEnumerable<UsePlan> plans, bool dryRun)
    {
        foreach (UsePlan plan in plans)
        {
            if (!plan.Changed)
            {
                output.WriteLine($"[{plan.Target.Label}] already configured — no change");
            }
            else if (dryRun)
            {
                output.WriteLine($"--- {plan.Config.Logical} (current)");
                output.WriteLine($"+++ {plan.Config.Logical} (proposed)");
                output.WriteLine("@@");
                foreach (string line in ConfigCodec.Decode(plan.Config.File.Bytes, plan.Target.Label)
                    .Split('\n'))
                {
                    output.WriteLine($"-{line}");
                }

                foreach (string line in ConfigCodec.Decode(plan.Output, plan.Target.Label).Split('\n'))
                {
                    output.WriteLine($"+{line}");
                }
            }
            else
            {
                output.WriteLine($"[{plan.Target.Label}] {plan.Action.ToString().ToLowerInvariant()}; backup: {plan.Backup!.Logical}");
            }
        }
    }

    private void PrintRevert(IEnumerable<RevertPlan> plans, bool dryRun)
    {
        foreach (RevertPlan plan in plans)
        {
            foreach ((string _, RecoveryEntry entry, DestinationBinding _) in plan.Entries)
            {
                output.WriteLine(
                    dryRun
                        ? $"[{plan.Target.Label}] would restore {entry.BackupPath}"
                        : $"[{plan.Target.Label}] restored from {entry.BackupPath}");
            }
        }
    }

    private sealed record Target(ClientInfo Client, ConfigScope Scope)
    {
        internal string Label => Client.Name == "claude"
            ? $"claude:{Scope.ToString().ToLowerInvariant()}"
            : Client.Name;
    }

    private interface ITransactionPlan
    {
        public Target Target { get; }

        public PathBinding Config { get; }

        public IEnumerable<DestinationBinding> Backups { get; }
    }

    private sealed record UsePlan(
        Target Target,
        PathBinding Config,
        DestinationBinding? Backup,
        byte[] Output,
        ConfigAction Action,
        ServerSpec Spec,
        RecoveryEntry? Prior,
        long Sequence,
        string SwappedAt) : ITransactionPlan
    {
        internal bool Changed => Action != ConfigAction.Unchanged;

        internal string ServerName { get; init; } = "tmux";

        internal string Repository { get; init; } = Path.GetFullPath(".");

        IEnumerable<DestinationBinding> ITransactionPlan.Backups =>
            Backup is null ? [] : [Backup];
    }

    private sealed record RevertPlan(
        Target Target,
        PathBinding Config,
        IReadOnlyList<(string Key, RecoveryEntry Entry, DestinationBinding Backup)> Entries,
        byte[] Output,
        int OutputMode) : ITransactionPlan
    {
        IEnumerable<DestinationBinding> ITransactionPlan.Backups => Entries.Select(entry => entry.Backup);
    }

    private sealed record UseTransaction(
        IReadOnlyList<UsePlan> Plans,
        RecoveryLedger Ledger,
        DestinationBinding? OptionalStateArtifact,
        long NextSequence)
    {
        internal DestinationBinding StateArtifact => OptionalStateArtifact
            ?? throw new InvalidOperationException("state directory does not exist");
    }

    private sealed record RevertTransaction(
        IReadOnlyList<RevertPlan> Plans,
        RecoveryLedger Ledger,
        DestinationBinding StateArtifact);

    private sealed record StateSnapshot(RecoveryLedger Ledger, DestinationBinding? Artifact);

    private sealed record StagedUse(
        UsePlan Plan,
        string Output,
        string Recovery,
        string? BackupStage);

    private sealed record StagedRevert(
        RevertPlan Plan,
        string Output,
        string ConfigRecovery,
        IReadOnlyList<string> BackupRecoveries);

    private sealed record ConfigCommit(PathBinding Binding, string Recovery, FileSnapshot? Committed);

    private sealed record ArtifactCommit(DestinationBinding Binding, FileSnapshot Committed);

    private sealed record BackupTakeAside(DestinationBinding Binding, string Recovery);
}
