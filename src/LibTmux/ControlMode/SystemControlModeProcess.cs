using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace LibTmux;

internal interface IControlModeProcess : IDisposable
{
    public bool HasExited { get; }

    public string StandardErrorTail => string.Empty;

    public Task WriteLineAsync(
        ReadOnlyMemory<char> command,
        CancellationToken cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken);

    public Task<string?> ReadLineAsync();

    public void CloseInput();

    public void Kill();

    public Task WaitForExitAsync(CancellationToken cancellationToken = default);

    public Task StopErrorPumpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SystemControlModeProcess : IControlModeProcess
{
    private readonly CancellationTokenSource _errorPumpCancellation = new();
    private readonly Task _errorPump;
    private readonly ControlModeLineReader _output;
    private readonly Process _process;
    private readonly RollingByteTail _standardError;

    internal SystemControlModeProcess(Process process, ControlModeLimits limits)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentNullException.ThrowIfNull(limits);
        _output = new ControlModeLineReader(
            process.StandardOutput.BaseStream,
            limits.MaxLineBytes);
        _standardError = new RollingByteTail(limits.StandardErrorTailBytes);
        _errorPump = PumpStandardErrorAsync(_errorPumpCancellation.Token);
    }

    public bool HasExited => _process.HasExited;

    public string StandardErrorTail => Encoding.UTF8.GetString(_standardError.Snapshot());

    public Task WriteLineAsync(
        ReadOnlyMemory<char> command,
        CancellationToken cancellationToken) =>
        _process.StandardInput.WriteLineAsync(command, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _process.StandardInput.FlushAsync(cancellationToken);

    public Task<string?> ReadLineAsync() => _output.ReadLineAsync();

    public void CloseInput() => _process.StandardInput.Close();

    public void Kill() => _process.Kill(entireProcessTree: false);

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.WaitForExitAsync(cancellationToken);

    public async Task StopErrorPumpAsync(CancellationToken cancellationToken)
    {
        await _errorPumpCancellation.CancelAsync().ConfigureAwait(false);
        await _errorPump.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _errorPumpCancellation.Cancel();
        _process.Dispose();
        _errorPumpCancellation.Dispose();
    }

    private async Task PumpStandardErrorAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int read = await _process.StandardError.BaseStream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _standardError.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class RollingByteTail
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _count;
    private int _start;

    internal RollingByteTail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new byte[capacity];
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (bytes.Length >= _buffer.Length)
            {
                bytes[^_buffer.Length..].CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                return;
            }

            int writeAt = (_start + _count) % _buffer.Length;
            int first = Math.Min(bytes.Length, _buffer.Length - writeAt);
            bytes[..first].CopyTo(_buffer.AsSpan(writeAt));
            bytes[first..].CopyTo(_buffer);
            int overflow = Math.Max(0, _count + bytes.Length - _buffer.Length);
            _start = (_start + overflow) % _buffer.Length;
            _count = Math.Min(_buffer.Length, _count + bytes.Length);
        }
    }

    internal byte[] Snapshot()
    {
        lock (_gate)
        {
            byte[] result = new byte[_count];
            int first = Math.Min(_count, _buffer.Length - _start);
            _buffer.AsSpan(_start, first).CopyTo(result);
            _buffer.AsSpan(0, _count - first).CopyTo(result.AsSpan(first));
            return result;
        }
    }
}
