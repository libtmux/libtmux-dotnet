using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace LibTmux.Workspace;

[UnsupportedOSPlatform("windows")]
internal static class WorkspaceHostScript
{
    internal static async Task<WorkspaceHostResult> RunAsync(WorkspaceHostCommand command, CancellationToken cancellationToken,
        WorkspaceOwnedProcessTree.Hooks? cleanupHooks = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        using OutputCapture capture = new(command.MaxOutputBytes);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new WorkspaceHostCanceledException(capture.Result(false, null),
                new OperationCanceledException(cancellationToken), cancellationToken);
        }

        ProcessStartInfo start = new("/bin/sh")
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command.Script);
        start.Environment.Clear();
        foreach ((string name, string value) in command.Environment)
        {
            start.Environment.Add(name, value);
        }

        using Process process = new() { StartInfo = start };
        using CancellationTokenSource timeout = new(command.Timeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using CancellationTokenSource stop = new();
        bool started = false;
        Task? work = null;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("The workspace host process did not start.");
            }

            process.StandardInput.Close();
            work = Task.WhenAll(
                DrainAsync(process.StandardOutput.BaseStream, capture, false, stop.Token),
                DrainAsync(process.StandardError.BaseStream, capture, true, stop.Token),
                process.WaitForExitAsync(stop.Token));
            await work.WaitAsync(operation.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(FormattableString.Invariant($"The workspace host command exited with code {process.ExitCode}."));
            }

            return capture.Result(true, process.ExitCode);
        }
        catch (Exception failure)
        {
            bool cancelled = failure is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (failure is OperationCanceledException && !cancelled && timeout.IsCancellationRequested)
            {
                failure = new TimeoutException("The workspace host command exceeded its process and output deadline.", failure);
            }

            if (started)
            {
                bool killed = false;
                try
                {
                    killed = await WorkspaceOwnedProcessTree.TryKillAsync(process, cleanupHooks).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    failure = new AggregateException(failure, cleanupFailure);
                }

                if (!killed)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    catch (Exception cleanupFailure)
                    {
                        failure = new AggregateException(failure, cleanupFailure);
                    }
                }
            }

            await stop.CancelAsync().ConfigureAwait(false);
            if (work is not null)
            {
                try
                {
                    await work.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception cleanupFailure)
                {
                    if (!ReferenceEquals(cleanupFailure, failure))
                    {
                        failure = new AggregateException(failure, cleanupFailure);
                    }
                }
            }

            if (started && !process.HasExited)
            {
                try
                {
                    using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    failure = new AggregateException(failure, cleanupFailure);
                }
            }

            WorkspaceHostResult result = capture.Result(started, started && process.HasExited ? process.ExitCode : null);
            if (cancelled)
            {
                throw new WorkspaceHostCanceledException(result, failure, cancellationToken);
            }

            throw new WorkspaceHostFailureException(result, failure);
        }
    }

    private static async Task DrainAsync(Stream stream, OutputCapture capture, bool error, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            capture.Append(error, buffer.AsSpan(0, read));
        }
    }

    private sealed class OutputCapture(int maximumBytes) : IDisposable
    {
        private readonly object _gate = new();
        private readonly MemoryStream _output = new();
        private readonly MemoryStream _error = new();
        private long _readBytes;
        private int _capturedBytes;

        internal void Append(bool error, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                _readBytes += bytes.Length;
                int retained = Math.Min(bytes.Length, maximumBytes - _capturedBytes);
                (error ? _error : _output).Write(bytes[..retained]);
                _capturedBytes += retained;
            }
        }

        internal WorkspaceHostResult Result(bool started, int? exitCode)
        {
            lock (_gate)
            {
                string output = Decode(_output.GetBuffer().AsSpan(0, (int)_output.Length), out int outputBytes);
                string error = Decode(_error.GetBuffer().AsSpan(0, (int)_error.Length), out int errorBytes);
                return new WorkspaceHostResult(started, exitCode, output, error, _readBytes - outputBytes - errorBytes);
            }
        }

        public void Dispose()
        {
            _output.Dispose();
            _error.Dispose();
        }

        private static string Decode(ReadOnlySpan<byte> bytes, out int consumed)
        {
            StringBuilder text = new(bytes.Length);
            Span<char> characters = stackalloc char[2];
            consumed = 0;
            int encodedBytes = 0;
            while (consumed < bytes.Length)
            {
                OperationStatus status = Rune.DecodeFromUtf8(bytes[consumed..], out Rune rune, out int count);
                if (status == OperationStatus.NeedMoreData || encodedBytes + rune.Utf8SequenceLength > bytes.Length)
                {
                    break;
                }

                text.Append(characters[..rune.EncodeToUtf16(characters)]);
                consumed += count;
                encodedBytes += rune.Utf8SequenceLength;
            }

            return text.ToString();
        }
    }
}

internal sealed class WorkspaceHostFailureException : Exception
{
    internal WorkspaceHostFailureException(WorkspaceHostResult result, Exception failure)
        : base("The workspace host command failed; external effects may remain.", failure) => Result = result;

    internal WorkspaceHostResult Result { get; }
}

internal sealed class WorkspaceHostCanceledException : OperationCanceledException
{
    internal WorkspaceHostCanceledException(WorkspaceHostResult result, Exception failure, CancellationToken cancellationToken)
        : base("The workspace host command was cancelled; external effects may remain.", failure, cancellationToken) => Result = result;

    internal WorkspaceHostResult Result { get; }
}
