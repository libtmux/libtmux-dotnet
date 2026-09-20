namespace LibTmux.Workspace;

/// <summary>Describes one explicitly enabled host shell command.</summary>
/// <remarks>
/// Execution uses /bin/sh -c with the supplied directory and environment. Shell
/// effects cannot be rolled back. Descendants that detach or outlive their shell
/// are outside containment.
/// </remarks>
public sealed class WorkspaceHostCommand
{
    internal WorkspaceHostCommand(
        string script,
        string workingDirectory,
        int maxOutputBytes,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (script.Contains('\0'))
        {
            throw new ArgumentException("The host command cannot contain NUL.", nameof(script));
        }

        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("The host working directory must be absolute.", nameof(workingDirectory));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The host timeout must be positive and fit the system timer.");
        }

        Script = script;
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        MaxOutputBytes = maxOutputBytes;
        Timeout = timeout;
        Environment = WorkspaceCollections.CopyEnvironment(environment, nameof(environment));
    }

    /// <summary>Gets the literal command passed to /bin/sh -c.</summary>
    public string Script { get; }

    /// <summary>Gets the absolute directory in which the command runs.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the combined stdout and stderr capture limit in bytes.</summary>
    public int MaxOutputBytes { get; }

    /// <summary>Gets the deadline for process exit and output completion.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the explicit environment; execution does not inherit process variables.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
}

/// <summary>Describes captured output and the observed exit of a host command.</summary>
public sealed class WorkspaceHostResult
{
    internal WorkspaceHostResult(bool started, int? exitCode, string standardOutput, string standardError, long droppedOutputBytes)
    {
        Started = started;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        DroppedOutputBytes = droppedOutputBytes;
    }

    /// <summary>Gets whether the shell started and may have left external effects.</summary>
    public bool Started { get; }

    /// <summary>Gets the observed shell exit code, or null when exit was not observed.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the retained UTF-8 stdout prefix.</summary>
    public string StandardOutput { get; }

    /// <summary>Gets the retained UTF-8 stderr prefix.</summary>
    public string StandardError { get; }

    /// <summary>Gets bytes read but omitted from capture; unread output after interruption is unknown.</summary>
    public long DroppedOutputBytes { get; }
}
