param(
    [Parameter(Position = 0)]
    [ValidateSet('get', 'master', 'game', 'chat', 'media', 'aux', 'mic')]
    [string]$Channel = 'get',

    [Parameter(Position = 1)]
    [string]$ActionOrPercent,

    [Parameter(Position = 2)]
    [ValidateSet('on', 'off', 'true', 'false')]
    [string]$MuteState
)

$ErrorActionPreference = 'Stop'

$channelIds = @{
    master = 'master'
    game   = 'game'
    chat   = 'chatRender'
    media  = 'media'
    aux    = 'aux'
    mic    = 'chatCapture'
}

function Invoke-CurlJson {
    param(
        [Parameter(Mandatory)]
        [string]$Url,

        [ValidateSet('GET', 'PUT')]
        [string]$Method = 'GET',

        [switch]$AllowInvalidCertificate
    )

    $arguments = @('--silent', '--show-error', '--fail-with-body', '--max-time', '5')
    if ($AllowInvalidCertificate) {
        $arguments += '--insecure'
    }
    if ($Method -eq 'PUT') {
        $arguments += @('--request', 'PUT')
    }
    $arguments += $Url

    $response = & curl.exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Local request failed (curl exit code $LASTEXITCODE): $Url"
    }
    return ($response -join "`n")
}

function Get-SonarAddress {
    $corePropsCandidates = @(
        (Join-Path $env:ProgramData 'SteelSeries\GG\coreProps.json'),
        (Join-Path $env:ProgramData 'SteelSeries\SteelSeries Engine 3\coreProps.json')
    )
    $corePropsPath = $corePropsCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $corePropsPath) {
        throw 'SteelSeries GG coreProps.json was not found. Start SteelSeries GG and enable Sonar.'
    }

    $coreProps = Get-Content -LiteralPath $corePropsPath -Raw | ConvertFrom-Json
    if (-not $coreProps.ggEncryptedAddress) {
        throw "ggEncryptedAddress is missing from $corePropsPath."
    }

    $subAppsJson = Invoke-CurlJson -Url "https://$($coreProps.ggEncryptedAddress)/subApps" -AllowInvalidCertificate
    $sonar = ($subAppsJson | ConvertFrom-Json).subApps.sonar
    if (-not $sonar.isEnabled) { throw 'Sonar is disabled in SteelSeries GG.' }
    if (-not $sonar.isRunning) { throw 'Sonar is not running.' }
    if (-not $sonar.isReady) { throw 'Sonar is running but is not ready.' }
    if (-not $sonar.metadata.webServerAddress) { throw 'GG did not publish the Sonar web server address.' }

    return [string]$sonar.metadata.webServerAddress
}

function Get-ClassicState {
    param([Parameter(Mandatory)][string]$SonarAddress)

    $json = Invoke-CurlJson -Url "$SonarAddress/volumeSettings/classic"
    return ($json | ConvertFrom-Json)
}

function Select-ChannelState {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$ApiChannel
    )

    if ($ApiChannel -eq 'master') {
        return $State.masters.classic
    }
    return $State.devices.$ApiChannel.classic
}

$sonarAddress = Get-SonarAddress
$mode = (Invoke-CurlJson -Url "$sonarAddress/mode/" | ConvertFrom-Json)
if ($mode -ne 'classic') {
    throw "Sonar is in '$mode' mode. This prototype intentionally operates on the classic mixer only."
}

if ($Channel -eq 'get') {
    $state = Get-ClassicState -SonarAddress $sonarAddress
    foreach ($name in @('master', 'game', 'chat', 'media', 'aux', 'mic')) {
        $item = Select-ChannelState -State $state -ApiChannel $channelIds[$name]
        [pscustomobject]@{
            Channel = $name.ToUpperInvariant()
            Volume  = [math]::Round(([double]$item.volume * 100), 1)
            Muted   = [bool]$item.muted
        }
    }
    return
}

$apiChannel = $channelIds[$Channel]
if ($ActionOrPercent -eq 'mute') {
    if (-not $MuteState) {
        throw 'Mute requires a state: on or off.'
    }
    $muted = $MuteState -in @('on', 'true')
    $mutedText = $muted.ToString().ToLowerInvariant()
    $null = Invoke-CurlJson -Url "$sonarAddress/volumeSettings/classic/$apiChannel/Mute/$mutedText" -Method PUT
} else {
    if ($null -eq $ActionOrPercent -or $ActionOrPercent -eq '') {
        throw 'A volume percentage from 0 to 100 is required.'
    }
    $percent = 0
    if (-not [int]::TryParse($ActionOrPercent, [ref]$percent) -or $percent -lt 0 -or $percent -gt 100) {
        throw "Invalid volume '$ActionOrPercent'. Use an integer from 0 to 100."
    }
    $volume = ([double]$percent / 100).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
    $null = Invoke-CurlJson -Url "$sonarAddress/volumeSettings/classic/$apiChannel/Volume/$volume" -Method PUT
}

$verifiedState = Get-ClassicState -SonarAddress $sonarAddress
$verified = Select-ChannelState -State $verifiedState -ApiChannel $apiChannel
[pscustomobject]@{
    Channel = $Channel.ToUpperInvariant()
    Volume  = [math]::Round(([double]$verified.volume * 100), 1)
    Muted   = [bool]$verified.muted
}
