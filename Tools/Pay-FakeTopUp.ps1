<#
.SYNOPSIS
    Pays (or fails) a coin top-up on a development server that runs the fake wallet.

.DESCRIPTION
    The development wallet (Meccha_Server FakeWalletProvider) has no checkout page: a payment is
    settled by the callback a real provider would send, signed with Wallet:CallbackSecret. This
    script sends that callback, so the game's top-up flow can be tested end to end. The game logs
    the exact command (payment id, amount, currency) when a top-up is opened on such a server.

    Development only. It needs the server's callback secret, which never goes in the game.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/Pay-FakeTopUp.ps1 -PaymentId 3f2a... -AmountMinor 499 -Currency USD

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/Pay-FakeTopUp.ps1 -PaymentId 3f2a... -AmountMinor 499 -Status failed
#>
param(
    [Parameter(Mandatory = $true)] [string] $PaymentId,
    [Parameter(Mandatory = $true)] [long] $AmountMinor,
    [string] $Currency = "USD",
    [ValidateSet("paid", "failed")] [string] $Status = "paid",
    # The server's config, read for Wallet:CallbackSecret and HttpPort.
    [string] $ServerConfig = (Join-Path $PSScriptRoot "..\..\Meccha_Server\server.json"),
    [string] $ServerHost = "localhost",
    # Overrides the values read from the config.
    [string] $Secret = "",
    [int] $HttpPort = 0
)

$ErrorActionPreference = "Stop"

if ($Secret -eq "" -or $HttpPort -eq 0) {
    if (-not (Test-Path $ServerConfig)) { throw "Server config not found: $ServerConfig (pass -ServerConfig, or -Secret and -HttpPort)." }

    # server.json may carry // comments; drop whole-line ones before parsing.
    $json = (Get-Content -Raw -Encoding UTF8 $ServerConfig) -split "`n" | Where-Object { $_ -notmatch '^\s*//' }
    $config = ($json -join "`n") | ConvertFrom-Json

    if ($Secret -eq "") { $Secret = $config.Wallet.CallbackSecret }
    if ($HttpPort -eq 0) { $HttpPort = [int]$config.HttpPort }

    if ($config.Wallet.Provider -ne "fake") { Write-Warning "Wallet:Provider is '$($config.Wallet.Provider)', not 'fake': the server will not take this callback." }
}

if ([string]::IsNullOrEmpty($Secret)) { throw "No Wallet:CallbackSecret: set one in the server config or pass -Secret." }
if ($HttpPort -le 0) { throw "No HttpPort: set one in the server config or pass -HttpPort." }

# The body the server checks: payment id, status, and the amount and currency it recorded.
$body = '{"paymentId":"' + $PaymentId + '","status":"' + $Status + '","amountMinor":' + $AmountMinor + ',"currency":"' + $Currency.ToUpperInvariant() + '"}'
$bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)

# X-Wallet-Signature = hex(HMAC-SHA256(secret, timestamp + "." + body)), lowercase.
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$prefix = [System.Text.Encoding]::ASCII.GetBytes("$timestamp.")
$signed = New-Object byte[] ($prefix.Length + $bodyBytes.Length)
[Array]::Copy($prefix, 0, $signed, 0, $prefix.Length)
[Array]::Copy($bodyBytes, 0, $signed, $prefix.Length, $bodyBytes.Length)

$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes($Secret))
try { $mac = $hmac.ComputeHash($signed) } finally { $hmac.Dispose() }
$signature = -join ($mac | ForEach-Object { $_.ToString("x2") })

$url = "http://${ServerHost}:$HttpPort/wallet/callback"
$headers = @{ "X-Wallet-Timestamp" = "$timestamp"; "X-Wallet-Signature" = $signature }

try {
    $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $url -Headers $headers -ContentType "application/json" -Body $bodyBytes
    Write-Host "Sent '$Status' for $PaymentId ($AmountMinor $Currency): HTTP $($response.StatusCode)"
}
catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { "no answer" }
    throw "The server refused the callback ($code). Check the payment id, amount, currency and secret. $($_.Exception.Message)"
}
