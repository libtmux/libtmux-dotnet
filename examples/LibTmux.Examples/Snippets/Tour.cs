using System.Runtime.Versioning;
using LibTmux.Testing;

namespace LibTmux.Examples.Snippets;

/// <summary>The tour that ships with the repository, one example per idea.</summary>
[UnsupportedOSPlatform("windows")]
public static class Tour
{
    /// <summary>Walks the server, session, window and pane relations.</summary>
    [Example("Walk the hierarchy a server holds")]
    public static async Task ShowHierarchy(Server server, Session session)
    {
        // ConnectionOptions is what the server was told, not something tmux answered.
        Console.WriteLine($"socket           {server.ConnectionOptions.SocketName}");
        Console.WriteLine($"session          {session.Name} ({session.Id})");
        // A server holds sessions, a session holds windows, a window holds
        // panes; each accessor returns a list without re-querying tmux.
        foreach (Window window in await session.GetWindowsAsync())
        {
            Console.WriteLine($"  window {window.Index,-3} {window.Name}");
            foreach (Pane pane in await window.GetPanesAsync())
            {
                Console.WriteLine($"    pane {pane.Index,-3} {pane.Width}x{pane.Height}");
            }
        }
    }

    /// <summary>Types a command into a pane and waits for what it printed.</summary>
    [Example("Type into a pane and wait for what it printed")]
    public static async Task RunACommand(Pane pane)
    {
        await pane.SendTextAsync("echo the-pane-ran-this");

        // tmux answers a command once it has accepted it, not once the shell
        // has finished, so the result is waited for rather than assumed.
        string text = await TmuxWait.UntilAsync(
            async token => string.Join('\n', await pane.CaptureAsync(cancellationToken: token)),
            captured => captured.Contains("the-pane-ran-this", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20));
        Console.WriteLine($"captured         {text.Contains("the-pane-ran-this", StringComparison.Ordinal)}");
    }

    /// <summary>Sets an option, reads it back, and reads an inherited one.</summary>
    [Example("Set an option, read it back, and read an inherited one")]
    public static async Task ReadAndWriteOptions(Window window)
    {
        await window.Options.SetAsync(new SetOptionRequest("automatic-rename", "off"));
        TmuxOption option = (await window.Options.GetAsync(
            new GetOptionRequest("automatic-rename")))[0];

        // tmux has no types, so a value carries what tmux said alongside the
        // readings that text supports.
        Console.WriteLine($"automatic-rename {option.Value.Raw} (flag {option.Value.Boolean})");

        // An option the window does not hold is inherited rather than missing,
        // and asking for inherited values is what shows it.
        IReadOnlyList<TmuxOption> inherited = await window.Options.GetAsync(
            new GetOptionRequest("mode-keys", includeInherited: true));
        Console.WriteLine($"mode-keys        {inherited[0].Value.Raw} (inherited)");
    }

    /// <summary>Sets a hook, runs it, and checks what it did.</summary>
    [Example("Make tmux run a command for itself")]
    public static async Task ReactToAnEvent(Server server)
    {
        // A hook is a tmux command tmux runs for itself when something
        // happens. Every hook is an array, even with one entry.
        TmuxHook hook = await server.Hooks.SetAsync(
            new SetHookRequest("alert-bell", "set-option -g @rang yes"));
        Console.WriteLine($"alert-bell       {hook.Values[0].Command}");

        await server.Hooks.RunAsync(new HookRequest("alert-bell"));
        IReadOnlyList<TmuxOption> rang = await server.Options.GetAsync(
            new GetOptionRequest("@rang", OptionScope.Session, global: true, quiet: true));
        Console.WriteLine($"hook ran         {rang.Count == 1}");
    }

    /// <summary>Filters what was read with ordinary LINQ.</summary>
    [Example("Filter what is there")]
    public static async Task FilterWhatIsThere(Session session)
    {
        await session.CreateWindowAsync(new NewWindowRequest(name: "build-one"));
        await session.CreateWindowAsync(new NewWindowRequest(name: "build-two"));

        // Ordinary filtering is LINQ over what was read.
        IReadOnlyList<Window> windows = await session.GetWindowsAsync();
        int building = windows.Count(window =>
            window.Name.StartsWith("build", StringComparison.Ordinal));
        Console.WriteLine($"building         {building}");
    }
}
