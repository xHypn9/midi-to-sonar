using System.IO;
using System.IO.Pipes;

namespace SonarMidiController.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(false, RuntimeProfile.MutexName);
        try
        {
            IsPrimaryInstance = _mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            IsPrimaryInstance = true;
        }
    }

    public bool IsPrimaryInstance { get; }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }
        _listenerTask = Task.Run(() => ListenAsync(_lifetime.Token));
    }

    public async Task<bool> SignalPrimaryInstanceAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    RuntimeProfile.PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(300).ConfigureAwait(false);
                await using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync("SHOW").ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    RuntimeProfile.PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        _mutex.Dispose();
        _lifetime.Dispose();
    }
}
