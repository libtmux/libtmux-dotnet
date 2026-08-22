using System.Buffers;
using System.Security.Cryptography;

namespace LibTmux.Internal;

internal static class PsmuxBinaryTrust
{
    private const int BufferSize = 81920;
    private const long MaximumBinaryBytes = 128L * 1024 * 1024;

    internal static async Task VerifyAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task verification = Task.Run(
            () => VerifyCoreAsync(path, expectedSha256, cancellationToken),
            CancellationToken.None);
        try
        {
            await verification.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveFutureFailure(verification);
            throw;
        }
    }

    private static async Task VerifyCoreAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var commit = new MarkerMatcher("66cf613"u8);
            var date = new MarkerMatcher("2026-08-18"u8);
            long total = 0;
            while (true)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumBinaryBytes)
                {
                    throw new NotSupportedException(
                        "The psmux executable exceeds the preview inspection limit.");
                }

                hash.AppendData(buffer, 0, read);
                commit.Advance(buffer.AsSpan(0, read));
                date.Advance(buffer.AsSpan(0, read));
            }

            byte[] actual = hash.GetHashAndReset();
            byte[] expected = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new NotSupportedException(
                    "The psmux executable SHA-256 does not match PsmuxConnectionOptions.");
            }

            if (!commit.Found || !date.Found)
            {
                throw new NotSupportedException(
                    "The trusted psmux executable does not contain the audited build markers.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The trusted psmux preview executable could not be read.",
                error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ObserveFutureFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class MarkerMatcher
    {
        private readonly byte[] _marker;
        private readonly int[] _prefix;
        private int _matched;

        internal MarkerMatcher(ReadOnlySpan<byte> marker)
        {
            _marker = marker.ToArray();
            _prefix = new int[_marker.Length];
            for (int index = 1, matched = 0; index < _marker.Length; index++)
            {
                while (matched > 0 && _marker[index] != _marker[matched])
                {
                    matched = _prefix[matched - 1];
                }

                if (_marker[index] == _marker[matched])
                {
                    matched++;
                }

                _prefix[index] = matched;
            }
        }

        internal bool Found { get; private set; }

        internal void Advance(ReadOnlySpan<byte> bytes)
        {
            if (Found)
            {
                return;
            }

            foreach (byte value in bytes)
            {
                while (_matched > 0 && value != _marker[_matched])
                {
                    _matched = _prefix[_matched - 1];
                }

                if (value == _marker[_matched])
                {
                    _matched++;
                }

                if (_matched == _marker.Length)
                {
                    Found = true;
                    return;
                }
            }
        }
    }
}
