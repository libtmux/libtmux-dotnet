namespace LibTmux.McpSwap;

internal static class McpSwapApp
{
    internal static int Execute(string[] arguments, TextWriter output, TextWriter error)
    {
        CommandOptions options;
        try
        {
            options = CommandLine.Parse(arguments);
        }
        catch (CommandLineException failure)
        {
            error.WriteLine($"mcp-swap: {failure.Message}");
            error.WriteLine(CommandLine.Usage);
            return 2;
        }

        if (options.Command == SwapCommand.Help)
        {
            output.WriteLine(CommandLine.Usage);
            output.WriteLine("sources: debug, release, run, published, path");
            output.WriteLine("clients: claude, codex, cursor, gemini, grok, agy, opencode, pi");
            return 0;
        }

        try
        {
            SwapRuntime runtime = SwapRuntime.Discover(options.Command == SwapCommand.Use);
            ClientCatalog catalog = ClientCatalog.Create(runtime.Home, runtime.ConfigHome);
            SwapService service = new(
                runtime,
                output,
                error,
                new SourceProvisioner(),
                new ServerPreflight());
            switch (options.Command)
            {
                case SwapCommand.Detect:
                    Detect(catalog, runtime, output);
                    break;
                case SwapCommand.Status:
                    Status(catalog, runtime, options, output, error);
                    break;
                case SwapCommand.Use:
                    service.Use(options);
                    break;
                case SwapCommand.Revert:
                    service.Revert(options);
                    break;
                case SwapCommand.Doctor:
                    Doctor(catalog, runtime, options, output);
                    break;
                default:
                    throw new InvalidOperationException($"unsupported command {options.Command}");
            }

            return 0;
        }
        catch (Exception failure) when (failure is IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException
            or SwapException
            or ArgumentException)
        {
            error.WriteLine($"mcp-swap: {failure.Message}");
            return 1;
        }
    }

    private static void Detect(ClientCatalog catalog, SwapRuntime runtime, TextWriter output)
    {
        foreach (ClientInfo client in catalog.Clients)
        {
            bool binary = ExecutableFinder.Find(client.Binary) is not null;
            bool config = File.Exists(client.ConfigPath);
            List<string> details = [];
            if (!binary)
            {
                details.Add("binary missing");
            }

            if (!config)
            {
                details.Add($"config missing: {client.ConfigPath}");
            }

            if (client.Name == "pi" && !Directory.Exists(PiAdapter(runtime)))
            {
                details.Add("needs the pi-mcp-adapter package; pi has no built-in MCP client");
            }

            string suffix = details.Count == 0 ? string.Empty : $"  ({string.Join(", ", details)})";
            output.WriteLine($"  [{(binary && config ? "yes" : " no")}] {client.Name,-9}{suffix}");
        }
    }

    internal static void Status(
        ClientCatalog catalog,
        SwapRuntime runtime,
        CommandOptions options,
        TextWriter output,
        TextWriter error)
    {
        RepositoryMetadata source = SourceResolver.ResolveMetadata(options);
        IReadOnlyList<ClientInfo> selected = options.Clients.Count > 0
            ? catalog.Select(options.Clients)
            : catalog.Clients.Where(
                client => File.Exists(client.ConfigPath)
                    && ExecutableFinder.Find(client.Binary) is not null).ToArray();
        foreach (ClientInfo client in selected)
        {
            if (!File.Exists(client.ConfigPath))
            {
                output.WriteLine($"[{client.Name}] no config at {client.ConfigPath}");
                continue;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(client.ConfigPath);
                if (client.Name == "claude")
                {
                    IEnumerable<ConfigScope> scopes = options.Scope is null
                        ? [ConfigScope.User, ConfigScope.Project]
                        : [options.Scope.Value];
                    bool shown = false;
                    foreach (ConfigScope scope in scopes)
                    {
                        ServerSpec? spec = ConfigCodec.Read(
                            client,
                            bytes,
                            source.ServerName,
                            source.Repository,
                            scope);
                        if (spec is null)
                        {
                            continue;
                        }

                        output.WriteLine(
                            $"[claude:{scope.ToString().ToLowerInvariant()}] {source.ServerName} = {Describe(spec, source.Repository, runtime)}");
                        shown = true;
                    }

                    if (!shown)
                    {
                        string label = options.Scope is null
                            ? "claude"
                            : $"claude:{options.Scope.Value.ToString().ToLowerInvariant()}";
                        output.WriteLine($"[{label}] no entry for '{source.ServerName}'");
                    }
                }
                else
                {
                    ServerSpec? spec = ConfigCodec.Read(
                        client,
                        bytes,
                        source.ServerName,
                        source.Repository,
                        ConfigScope.User);
                    output.WriteLine(
                        spec is null
                            ? $"[{client.Name}] no entry for '{source.ServerName}'"
                            : $"[{client.Name}] {source.ServerName} = {Describe(spec, source.Repository, runtime)}");
                }
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException)
            {
                error.WriteLine($"[{client.Name}] {failure.Message}");
            }
        }
    }

    private static void Doctor(
        ClientCatalog catalog,
        SwapRuntime runtime,
        CommandOptions options,
        TextWriter output)
    {
        RepositoryMetadata source = SourceResolver.ResolveMetadata(options);
        output.WriteLine("mcp-swap doctor");
        output.WriteLine($"  repo:   {source.Repository}");
        output.WriteLine($"  server: {source.ServerName}  (derived default; override with --server)");
        output.WriteLine("  entries by CLI:");
        bool any = false;
        SortedSet<string> repositoryNames = new(StringComparer.Ordinal);
        foreach (ClientInfo client in catalog.Clients.Where(
            client => File.Exists(client.ConfigPath)))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(client.ConfigPath);
                IReadOnlyDictionary<string, ServerSpec> specs = ConfigCodec.ReadAll(
                    client,
                    bytes,
                    source.Repository);
                if (specs.TryGetValue(source.ServerName, out ServerSpec? selected))
                {
                    output.WriteLine(
                        $"    [{client.Name}] {source.ServerName} = {Describe(selected, source.Repository, runtime)}");
                    any = true;
                }

                foreach ((string name, ServerSpec spec) in specs)
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

                    repositoryNames.Add(name);
                    if (!string.Equals(name, source.ServerName, StringComparison.Ordinal))
                    {
                        output.WriteLine($"    [{client.Name}] {name} = local: this repo  (other name)");
                        any = true;
                    }
                }
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException)
            {
                output.WriteLine($"    [{client.Name}] config unreadable: {failure.Message}");
            }
        }

        if (!any)
        {
            output.WriteLine("    (no CLI currently has the derived server entry)");
        }

        if (repositoryNames.Count > 0 && !repositoryNames.Contains(source.ServerName))
        {
            string selected = repositoryNames.Min!;
            output.WriteLine(
                $"  ! server name mismatch: this repo is registered as [{string.Join(", ", repositoryNames)}], not '{source.ServerName}' — use --server {selected}");
        }

        RecoveryLedger ledger = new();
        if (NativeFileSystem.TryLStat(runtime.StateFile, out _))
        {
            try
            {
                ledger = LedgerCodec.Read(runtime.StateFile);
                output.WriteLine("  outstanding swaps (un-reverted):");
                foreach (RecoveryEntry entry in ledger.Entries.Values.OrderBy(entry => entry.Sequence))
                {
                    string missing = NativeFileSystem.TryLStat(entry.BackupPath, out _)
                        ? string.Empty
                        : "  ! BACKUP MISSING — revert would fail for this entry";
                    output.WriteLine(
                        $"    {LedgerCodec.StateKey(entry.Client, entry.Scope)}  swapped_at={entry.SwappedAt}{missing}");
                }
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException)
            {
                output.WriteLine($"  ! recovery state unreadable: {failure.Message}");
            }
        }

        HashSet<string> referenced = ledger.Entries.Values
            .Select(entry => entry.BackupPath)
            .ToHashSet(StringComparer.Ordinal);
        List<string> orphans = [];
        foreach (ClientInfo client in catalog.Clients)
        {
            string? directory = Path.GetDirectoryName(client.ConfigPath);
            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            orphans.AddRange(Directory.GetFiles(
                directory,
                Path.GetFileName(client.ConfigPath) + BackupSuffixPattern(),
                SearchOption.TopDirectoryOnly).Where(path => !referenced.Contains(path)));
        }

        if (orphans.Count > 0)
        {
            long bytes = orphans.Sum(path => new FileInfo(path).Length);
            output.WriteLine(
                $"  orphaned backups: {orphans.Count} file(s), {bytes} bytes not tracked by state — inspect before deleting");
        }

        foreach ((string variable, string client) in AuthEnvironment)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                output.WriteLine(
                    $"  ! {variable} overrides {client}'s stored login — unset it to use subscription or OAuth auth");
            }
        }
    }

    private static string Describe(ServerSpec spec, string repository, SwapRuntime runtime)
    {
        string joined = string.Join(' ', spec.Arguments);
        string detail = spec.Command + (joined.Length == 0 ? string.Empty : " " + joined);
        string? local = LocalRepository(spec);
        if (local is not null)
        {
            return string.Equals(local, repository, StringComparison.Ordinal)
                ? detail + "  (this repo)"
                : detail + $"  ({local})";
        }

        string releases = Path.Combine(runtime.StateHome, "tmux-mcp-dev", "releases");
        return spec.Command.StartsWith(releases + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? detail + "  (published release)"
            : detail;
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

        string normalized = spec.Command.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string marker = $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}";
        int index = normalized.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? normalized[..index] : null;
    }

    private static string PiAdapter(SwapRuntime runtime) =>
        Path.Combine(runtime.Home, ".pi", "agent", "npm", "node_modules", "pi-mcp-adapter");

    private static string BackupSuffixPattern() => ".bak.mcp-swap-dotnet-*";

    private static readonly KeyValuePair<string, string>[] AuthEnvironment =
    [
        new("ANTHROPIC_API_KEY", "claude"),
        new("OPENAI_API_KEY", "codex"),
        new("GEMINI_API_KEY", "gemini"),
        new("GOOGLE_API_KEY", "gemini"),
        new("XAI_API_KEY", "grok"),
        new("GROK_API_KEY", "grok"),
    ];
}
