namespace SonarController.Core;

public interface ISonarController
{
    Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<double> GetVolumeAsync(SonarChannel channel, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(SonarChannel channel, double value, CancellationToken cancellationToken = default);
    Task<bool> GetMuteAsync(SonarChannel channel, CancellationToken cancellationToken = default);
    Task SetMuteAsync(SonarChannel channel, bool state, CancellationToken cancellationToken = default);
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default);
}
