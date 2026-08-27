using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SonarController.Core;

public sealed class SteelSeriesSonarController : ISonarController, IDisposable
{
    private static readonly SonarChannel[] AllChannels = Enum.GetValues<SonarChannel>();
    private readonly string _corePropsPath;
    private readonly HttpClient _httpClient;
    private readonly ISonarLogger _logger;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private Uri? _sonarBaseUri;
    private bool? _lastConnectionState;
    private bool _disposed;

    public SteelSeriesSonarController(
        ISonarLogger? logger = null,
        string? corePropsPath = null,
        HttpMessageHandler? handler = null)
    {
        _logger = logger ?? NullSonarLogger.Instance;
        _corePropsPath = corePropsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries",
            "GG",
            "coreProps.json");

        handler ??= new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is not null && IsLoopback(request.RequestUri)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public async Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await SendJsonAsync(
                HttpMethod.Get,
                "volumeSettings/classic",
                cancellationToken);
            var root = document.RootElement;
            var states = new Dictionary<SonarChannel, SonarChannelState>(AllChannels.Length);

            foreach (var channel in AllChannels)
            {
                JsonElement classic;
                if (channel == SonarChannel.Master)
                {
                    classic = root.GetProperty("masters").GetProperty("classic");
                }
                else
                {
                    classic = root
                        .GetProperty("devices")
                        .GetProperty(SonarChannelMap.ToApiId(channel))
                        .GetProperty("classic");
                }

                states[channel] = new SonarChannelState(
                    classic.GetProperty("volume").GetDouble(),
                    classic.GetProperty("muted").GetBoolean());
            }

            SetConnectionState(true);
            return new SonarSnapshot(states, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetConnectionState(false);
            throw Wrap(ex);
        }
    }

    public async Task<double> GetVolumeAsync(
        SonarChannel channel,
        CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken))[channel].Volume;

    public async Task SetVolumeAsync(
        SonarChannel channel,
        double value,
        CancellationToken cancellationToken = default)
    {
        value = Math.Clamp(value, 0d, 1d);
        var number = value.ToString("0.####", CultureInfo.InvariantCulture);
        var apiChannel = SonarChannelMap.ToApiId(channel);

        try
        {
            using var _ = await SendJsonAsync(
                HttpMethod.Put,
                $"volumeSettings/classic/{apiChannel}/Volume/{number}",
                cancellationToken);
            SetConnectionState(true);
            _logger.Info("Sonar", $"{channel.ToString().ToUpperInvariant()} -> {value:0.00}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetConnectionState(false);
            throw Wrap(ex);
        }
    }

    public async Task<bool> GetMuteAsync(
        SonarChannel channel,
        CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken))[channel].IsMuted;

    public async Task SetMuteAsync(
        SonarChannel channel,
        bool state,
        CancellationToken cancellationToken = default)
    {
        var apiChannel = SonarChannelMap.ToApiId(channel);
        var stateText = state.ToString().ToLowerInvariant();

        try
        {
            using var _ = await SendJsonAsync(
                HttpMethod.Put,
                $"volumeSettings/classic/{apiChannel}/Mute/{stateText}",
                cancellationToken);
            SetConnectionState(true);
            _logger.Info("Sonar", $"{channel.ToString().ToUpperInvariant()} mute -> {state}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetConnectionState(false);
            throw Wrap(ex);
        }
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetSnapshotAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        Exception? firstError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var baseUri = await DiscoverAsync(attempt > 0, cancellationToken);
                var uri = new Uri(baseUri, relativePath);
                using var request = new HttpRequestMessage(method, uri);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new SonarApiException(
                        $"Sonar returned HTTP {(int)response.StatusCode} for {method} /{relativePath}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
            {
                firstError = ex;
                InvalidateDiscovery();
            }
        }

        throw new SonarApiException(
            "SteelSeries Sonar is not reachable after rediscovery.",
            firstError ?? new InvalidOperationException("Unknown Sonar connection error."));
    }

    private async Task<Uri> DiscoverAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _sonarBaseUri is not null)
        {
            return _sonarBaseUri;
        }

        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && _sonarBaseUri is not null)
            {
                return _sonarBaseUri;
            }

            if (!File.Exists(_corePropsPath))
            {
                throw new SonarApiException(
                    "SteelSeries GG coreProps.json was not found. Start GG and enable Sonar.");
            }

            var coreJson = await File.ReadAllTextAsync(_corePropsPath, cancellationToken);
            using var coreDocument = JsonDocument.Parse(coreJson);
            var ggAddress = coreDocument.RootElement
                .GetProperty("ggEncryptedAddress")
                .GetString();

            if (string.IsNullOrWhiteSpace(ggAddress))
            {
                throw new SonarApiException("GG did not publish ggEncryptedAddress.");
            }

            var ggUri = CreateLoopbackUri($"https://{ggAddress}/subApps");
            _logger.Info("Sonar", "GG detected");

            using var response = await _httpClient.GetAsync(ggUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var subApps = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            var sonar = subApps.RootElement.GetProperty("subApps").GetProperty("sonar");
            if (!sonar.GetProperty("isEnabled").GetBoolean())
            {
                throw new SonarApiException("Sonar is disabled in SteelSeries GG.");
            }
            if (!sonar.GetProperty("isRunning").GetBoolean())
            {
                throw new SonarApiException("Sonar is not running.");
            }
            if (!sonar.GetProperty("isReady").GetBoolean())
            {
                throw new SonarApiException("Sonar is not ready yet.");
            }

            var address = sonar.GetProperty("metadata").GetProperty("webServerAddress").GetString();
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new SonarApiException("GG did not publish Sonar's web server address.");
            }

            if (!address.Contains("://", StringComparison.Ordinal))
            {
                address = $"http://{address}";
            }

            var sonarUri = CreateLoopbackUri(address.TrimEnd('/') + "/");
            using var modeResponse = await _httpClient.GetAsync(new Uri(sonarUri, "mode/"), cancellationToken);
            modeResponse.EnsureSuccessStatusCode();
            var mode = await modeResponse.Content.ReadFromJsonAsync<string>(
                cancellationToken: cancellationToken);
            if (!string.Equals(mode, "classic", StringComparison.OrdinalIgnoreCase))
            {
                throw new SonarApiException(
                    $"Sonar is in '{mode}' mode. This prototype controls the classic mixer.");
            }

            _sonarBaseUri = sonarUri;
            _logger.Info("Sonar", $"API discovered at {sonarUri.Authority}");
            return sonarUri;
        }
        catch (JsonException ex)
        {
            throw new SonarApiException("GG discovery returned an unexpected JSON format.", ex);
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private static Uri CreateLoopbackUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsLoopback(uri))
        {
            throw new SonarApiException("Refusing to connect: the discovered address is not loopback.");
        }
        return uri;
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private void InvalidateDiscovery() => _sonarBaseUri = null;

    private void SetConnectionState(bool connected)
    {
        if (_lastConnectionState == connected)
        {
            return;
        }
        _lastConnectionState = connected;
        _logger.Info("Sonar", connected ? "Connected" : "Disconnected");
    }

    private static SonarApiException Wrap(Exception exception) =>
        exception as SonarApiException
        ?? new SonarApiException("SteelSeries Sonar communication failed.", exception);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _httpClient.Dispose();
        _discoveryLock.Dispose();
    }
}
