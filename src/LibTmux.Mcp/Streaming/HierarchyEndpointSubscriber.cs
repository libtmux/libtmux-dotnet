namespace LibTmux.Mcp;

/// <summary>Coalesces hierarchy invalidations for one subscriber.</summary>
internal sealed class HierarchyEndpointSubscriber(
    Func<IReadOnlyList<string>, Task> announce,
    Action<Exception> reportFailure)
{
    private readonly object _deliveryGate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _retirement = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _delivering;
    private bool _retired;

    internal Dictionary<string, byte> Resources { get; } = new(StringComparer.Ordinal);

    internal void Enqueue(IReadOnlyList<string> resources)
    {
        bool startDelivery = false;
        lock (_deliveryGate)
        {
            if (_retired)
            {
                return;
            }

            _pending.UnionWith(resources);
            if (!_delivering)
            {
                _delivering = true;
                startDelivery = true;
            }
        }

        if (startDelivery)
        {
            _ = ObserveDeliveryAsync(Task.Run(DeliverAsync));
        }
    }

    internal void RemovePending(string uri)
    {
        lock (_deliveryGate)
        {
            _pending.Remove(uri);
        }
    }

    internal void Retire()
    {
        bool cancel;
        lock (_deliveryGate)
        {
            cancel = !_retired;
            _retired = true;
            _pending.Clear();
        }

        if (cancel)
        {
            _retirement.TrySetResult();
        }
    }

    private async Task DeliverAsync()
    {
        while (true)
        {
            string[] resources;
            lock (_deliveryGate)
            {
                if (_retired || _pending.Count == 0)
                {
                    _delivering = false;
                    return;
                }

                resources = [.. _pending];
                _pending.Clear();
            }

            try
            {
                Task delivery = announce(resources);
                if (await Task.WhenAny(delivery, _retirement.Task).ConfigureAwait(false)
                    != delivery)
                {
                    _ = ObserveDeliveryAsync(delivery);
                    return;
                }

                await delivery.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Report(error);
            }
        }
    }

    private async Task ObserveDeliveryAsync(Task delivery)
    {
        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private void Report(Exception error)
    {
        try
        {
            reportFailure(error);
        }
        catch (Exception)
        {
            // A logger cannot be allowed to fault detached delivery.
        }
    }
}
