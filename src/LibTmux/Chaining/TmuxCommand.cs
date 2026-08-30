using System.Collections.ObjectModel;

namespace LibTmux;

/// <summary>One tmux command and the arguments it carries.</summary>
/// <remarks>
/// The typed methods on <see cref="Server" />, <see cref="Session" />,
/// <see cref="Window" />, and <see cref="Pane" /> each run one command and
/// return what it produced. This is that same command as a value, so several
/// can be handed to tmux together through <see cref="TmuxChain" /> rather than
/// one process at a time.
/// </remarks>
public sealed record TmuxCommand
{
    private string _name = null!;
    private ReadOnlyCollection<string> _arguments = null!;

    /// <summary>Initializes a tmux command.</summary>
    /// <param name="Name">The tmux command name.</param>
    /// <param name="Arguments">Its arguments.</param>
    public TmuxCommand(string Name, IReadOnlyList<string> Arguments)
    {
        this.Name = Name;
        this.Arguments = Arguments;
    }

    /// <summary>Gets the tmux command name.</summary>
    public string Name
    {
        get => _name;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            ValidateToken(value, nameof(Name));
            _name = value;
        }
    }

    /// <summary>Gets the arguments, separated as tmux will receive them.</summary>
    public IReadOnlyList<string> Arguments
    {
        get => _arguments;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            string[] copy = [.. value];
            if (copy.Any(static argument => argument is null))
            {
                throw new ArgumentException("A tmux command argument cannot be null.", nameof(value));
            }

            foreach (string argument in copy)
            {
                ValidateToken(argument, nameof(Arguments));
            }

            _arguments = Array.AsReadOnly(copy);
        }
    }

    /// <summary>Creates a command from its name and arguments.</summary>
    /// <param name="name">The tmux command name.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentException">
    /// The name is empty, an argument is null, or a token contains NUL.
    /// </exception>
    public static TmuxCommand Create(string name, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new TmuxCommand(name, arguments);
    }

    /// <summary>Gets the server generation this command's target belongs to.</summary>
    /// <remarks>
    /// A tmux ID such as <c>%2</c> is only meaningful on the server that issued
    /// it; a restarted server reuses those IDs for different objects. A command
    /// built from an entity therefore records which server the entity was read
    /// from, and <see cref="TmuxChain.ExecuteAsync" /> refuses to run it against
    /// a different one.
    ///
    /// Null means the command names no entity -- a raw command, or one whose
    /// target is a name rather than an ID -- and carries no such requirement.
    /// </remarks>
    public ServerGeneration? RequiredGeneration { get; init; }

    /// <summary>Returns this command the way tmux receives it.</summary>
    /// <returns>The command name followed by its arguments.</returns>
    public IReadOnlyList<string> ToArguments() => [Name, .. Arguments];

    /// <inheritdoc />
    public bool Equals(TmuxCommand? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && RequiredGeneration == other.RequiredGeneration
        && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(RequiredGeneration);
        foreach (string argument in Arguments)
        {
            hash.Add(argument, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Deconstructs the command into its name and arguments.</summary>
    /// <param name="Name">The command name.</param>
    /// <param name="Arguments">The command arguments.</param>
    public void Deconstruct(out string Name, out IReadOnlyList<string> Arguments)
    {
        Name = this.Name;
        Arguments = this.Arguments;
    }

    private static void ValidateToken(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tmux command tokens cannot contain NUL.", parameterName);
        }
    }
}
