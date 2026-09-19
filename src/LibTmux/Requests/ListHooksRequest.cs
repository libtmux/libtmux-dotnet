using System.Runtime.Versioning;

namespace LibTmux;

/// <summary>Describes one <c>show-hooks</c> invocation.</summary>
public sealed record ListHooksRequest : ITmuxRequest<TmuxHooks>
{
    /// <summary>Gets the scope to read, or null for the owner's own.</summary>
    public OptionScope? Scope { get; init; }

    /// <summary>Gets whether the global table is read instead of the local one.</summary>
    public bool Global { get; init; }

    /// <summary>Returns a hook listing as one tmux command.</summary>
    /// <param name="hooks">The hooks handle whose scope is listed.</param>
    /// <returns>The command, ready to add to a <see cref="TmuxChain" />.</returns>
    /// <remarks>
    /// A chain returns one combined output stream, so batch a listing to see
    /// what the same invocation just installed. <see cref="TmuxHooks.GetAllAsync" />
    /// answers the same question with the hooks already parsed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="hooks" /> is null.</exception>
    [UnsupportedOSPlatform("windows")]
    public TmuxCommand ToCommand(TmuxHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return TmuxChaining.Command([.. hooks.BuildListArguments(this)]) with
        {
            RequiredGeneration = hooks.Generation,
        };
    }
}
