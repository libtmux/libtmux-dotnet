namespace LibTmux;

/// <summary>Turns a request record into a command a chain can carry.</summary>
/// <remarks>
/// <para>
/// Every overload builds its command with the same code the one-shot method
/// uses, so a chained call and a direct call send identical arguments rather
/// than two descriptions that have to be kept in step.
/// </para>
/// <para>
/// What each overload takes is decided by what tmux needs, not by taste. A
/// session names no target because the session is what is being made; a window
/// names the session that will hold it; keys name the pane, because which
/// flags tmux accepts depends on the server version and the pane is what knows
/// it.
/// </para>
/// </remarks>
public static partial class TmuxChaining
{
    private static TmuxCommand Command(string[] arguments) =>
        new(arguments[0], arguments[1..]);
}
