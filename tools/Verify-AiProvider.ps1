[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'tmp\provider-probe'))
if (-not $tempRoot.StartsWith($workspaceRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Probe directory escaped the workspace: $tempRoot"
}

$capturePath = Join-Path $tempRoot 'request.json'
$listenerJob = $null
$codexProcess = $null

try {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    $portProbe = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $portProbe.Start()
    $port = ([System.Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()

    $listenerJob = Start-Job -ArgumentList $port, $capturePath -ScriptBlock {
        param($ListenPort, $OutputPath)

        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            $ListenPort)
        try {
            $listener.Start()
            while (-not (Test-Path -LiteralPath $OutputPath)) {
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $reader = [System.IO.StreamReader]::new(
                        $stream,
                        [System.Text.Encoding]::ASCII,
                        $false,
                        4096,
                        $true)

                    $requestLine = $reader.ReadLine()
                    $authorization = $null
                    while ($true) {
                        $line = $reader.ReadLine()
                        if ([string]::IsNullOrEmpty($line)) { break }
                        if ($line.StartsWith('Authorization:', [System.StringComparison]::OrdinalIgnoreCase)) {
                            $authorization = $line.Substring('Authorization:'.Length).Trim()
                        }
                    }

                    if ($requestLine -like 'GET /models*') {
                        $status = '200 OK'
                        $body = '{"object":"list","data":[{"id":"probe_model","object":"model","created":0,"owned_by":"probe"}]}'
                    }
                    else {
                        [pscustomobject]@{
                            requestLine = $requestLine
                            authorization = $authorization
                        } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
                        $status = '500 Internal Server Error'
                        $body = '{"error":{"message":"provider route probe complete"}}'
                    }

                    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                    $headers = "HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
                    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($headers)
                    $stream.Write($headerBytes, 0, $headerBytes.Length)
                    $stream.Write($bodyBytes, 0, $bodyBytes.Length)
                    $stream.Flush()
                }
                finally {
                    $client.Dispose()
                }
            }
        }
        finally {
            $listener.Stop()
        }
    }

    $codexCommand = Get-Command codex -ErrorAction Stop
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $codexCommand.Source
    $startInfo.WorkingDirectory = $tempRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['CODEX_PROFILE_LAUNCHER_API_KEY'] = 'probe-key-not-secret'

    $arguments = @(
        'exec',
        '--skip-git-repo-check',
        '--ephemeral',
        '--ignore-rules',
        '--json',
        '-C', $tempRoot,
        '-c', 'model="probe_model"',
        '-c', 'model_provider="probe_provider"',
        '-c', 'model_providers.probe_provider.name="Local route probe"',
        '-c', ('model_providers.probe_provider.base_url="http://127.0.0.1:{0}"' -f $port),
        '-c', 'model_providers.probe_provider.env_key="CODEX_PROFILE_LAUNCHER_API_KEY"',
        '-c', 'model_providers.probe_provider.wire_api="responses"',
        'Reply with OK'
    )
    foreach ($argument in $arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $codexProcess = [System.Diagnostics.Process]::Start($startInfo)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $capturePath) -and [DateTime]::UtcNow -lt $deadline) {
        if ($codexProcess.HasExited) {
            $stderr = $codexProcess.StandardError.ReadToEnd()
            throw "Codex exited before reaching the provider: $stderr"
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $capturePath)) {
        throw "Timed out waiting for Codex provider request after $TimeoutSeconds seconds."
    }

    $capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
    if ($capture.requestLine -ne 'POST /responses HTTP/1.1') {
        throw "Expected POST /responses HTTP/1.1, received: $($capture.requestLine)"
    }
    if ($capture.authorization -ne 'Bearer probe-key-not-secret') {
        throw 'The provider request did not contain the expected dedicated API key.'
    }

    [pscustomobject]@{
        passed = $true
        codex = $codexCommand.Source
        request = $capture.requestLine
        dedicatedApiKeyInjected = $true
    } | ConvertTo-Json
}
finally {
    if ($null -ne $codexProcess -and -not $codexProcess.HasExited) {
        $codexProcess.Kill($true)
        $codexProcess.WaitForExit(5000) | Out-Null
    }
    if ($null -ne $listenerJob) {
        Stop-Job -Job $listenerJob -ErrorAction SilentlyContinue
        Remove-Job -Job $listenerJob -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
