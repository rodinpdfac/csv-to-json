using namespace System
param($Request, $TriggerMetadata)

# ========= Config =========
$CsvBlobUrl   = $env:CSV_BLOB_URL

# Use caminhos graváveis (Windows Functions): TEMP / home / local temp
$TempRoot     = $env:TEMP
if (-not $TempRoot) { $TempRoot = "D:\local\Temp" }

$CsvPath      = $env:CSV_PATH
if (-not $CsvPath) { $CsvPath = Join-Path $TempRoot "ProvisioningUsers.csv" }

$LogPath      = $env:LOG_PATH
if (-not $LogPath) { $LogPath = Join-Path $TempRoot "logs\Set-Birthday-Result.log" }

$Delimiter    = $env:CSV_DELIMITER; if (-not $Delimiter) { $Delimiter = ";" }

# Colunas (podem ser sobrescritas por env var)
$UpnCol       = $env:CSV_UPN_COL;   if (-not $UpnCol)  { $UpnCol  = "User Principal Name" }
$BdayCol      = $env:CSV_BDAY_COL;  if (-not $BdayCol) { $BdayCol = "Birthday" }

# Graph (delegated via Refresh Token)
$TenantId     = $env:GRAPH_TENANT_ID
$ClientId     = $env:GRAPH_CLIENT_ID
$RefreshToken = $env:GRAPH_REFRESH_TOKEN  # OBRIGATÓRIO (vem do device code uma vez)

# ========= Helpers =========
function Write-Log([string]$msg) {
  if (-not $LogPath) { return }
  $line = "$(Get-Date -Format s) $msg"
  try {
    $dir = Split-Path $LogPath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Add-Content -Path $LogPath -Value $line -Encoding utf8
  } catch {
    Write-Host "LOG_FAIL: $($_.Exception.Message)"
  }
}

function Get-JwtPayload([string]$jwt) {
  try {
    $parts = $jwt.Split('.')
    if ($parts.Count -lt 2) { return $null }
    $p = $parts[1].Replace('-', '+').Replace('_', '/')
    switch ($p.Length % 4) { 2 {$p+='=='} 3 {$p+='='} }
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p))
    return ($json | ConvertFrom-Json)
  } catch { return $null }
}

function Get-ManagedIdentityToken {
  param([Parameter(Mandatory)] [string]$Resource)

  if ($env:MSI_ENDPOINT -and $env:MSI_SECRET) {
    $uri = "$($env:MSI_ENDPOINT)?resource=$([uri]::EscapeDataString($Resource))&api-version=2017-09-01"
    $headers = @{ "Secret" = $env:MSI_SECRET }
    return (Invoke-RestMethod -Method GET -Uri $uri -Headers $headers).access_token
  }

  $msiUri = "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=$([uri]::EscapeDataString($Resource))"
  return (Invoke-RestMethod -Headers @{ Metadata = "true" } -Method GET -Uri $msiUri).access_token
}

function Download-BlobToLocal {
  param(
    [Parameter(Mandatory)] [string]$BlobUrl,
    [Parameter(Mandatory)] [string]$LocalPath
  )

  $token = Get-ManagedIdentityToken -Resource "https://storage.azure.com/"
  $dir = Split-Path $LocalPath
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

  Invoke-RestMethod -Method GET -Uri $BlobUrl -Headers @{
    Authorization  = "Bearer $token"
    "x-ms-version" = "2020-10-02"
  } -OutFile $LocalPath
}

function Get-GraphTokenFromRefreshToken {
  if (-not $TenantId -or -not $ClientId -or -not $RefreshToken) {
    throw "GRAPH_TENANT_ID / GRAPH_CLIENT_ID / GRAPH_REFRESH_TOKEN não definidos."
  }

  $tokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
  $scope = "https://graph.microsoft.com/.default offline_access"

  $body = @{
    client_id     = $ClientId
    grant_type    = "refresh_token"
    refresh_token = $RefreshToken
    scope         = $scope
  }

  $resp = Invoke-RestMethod -Method POST -Uri $tokenUri -Body $body -ContentType "application/x-www-form-urlencoded"

  if ($resp.refresh_token) {
    Write-Log "INFO: refresh_token retornou (possível rotação). Atualize o secret GRAPH_REFRESH_TOKEN se necessário."
  }

  return $resp.access_token
}

function Normalize-String([string]$s) {
  if ($null -eq $s) { return "" }
  return ($s + "").Trim().Trim('"').Trim("'")
}

# Parse robusto que retorna DateTime ou $null (evita MinValue 0001-01-01).
# Aceita DD/MM/YYYY, yyyy-MM-dd, MM/dd/yyyy e fallback por regex para DD/MM/YYYY
# (BOM e caracteres invisíveis são removidos antes do parse).
function Parse-DateOrNull([string]$s) {
  $s = Normalize-String $s
  if (-not $s) { return $null }

  # Remove BOM (UTF-8) e non-breaking space que podem vir do CSV
  $s = $s.TrimStart([char]0xFEFF).TrimStart([char]0x00A0).Trim()

  $formats = @(
    "yyyy-MM-dd",
    "dd/MM/yyyy",
    "d/MM/yyyy",
    "dd/M/yyyy",
    "d/M/yyyy",
    "dd-MM-yyyy",
    "d-M-yyyy",
    "MM/dd/yyyy",
    "M/d/yyyy",
    "yyyy/MM/dd"
  )

  $dt = [datetime]::MinValue
  $ok = [datetime]::TryParseExact(
    $s,
    $formats,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref]$dt
  )
  if ($ok) { return $dt }

  # Fallback: parse explícito DD/MM/YYYY (regex) para CSV brasileiro
  if ($s -match '^(\d{1,2})[/\-\.](\d{1,2})[/\-\.](\d{4})$') {
    $day   = [int]$Matches[1]
    $month = [int]$Matches[2]
    $year  = [int]$Matches[3]
    if ($day -ge 1 -and $day -le 31 -and $month -ge 1 -and $month -le 12 -and $year -ge 1900 -and $year -le 2100) {
      try {
        return [datetime]::new($year, $month, $day)
      } catch {
        # data inexistente (ex.: 31/02)
      }
    }
  }

  $ok2 = [datetime]::TryParse(
    $s,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref]$dt
  )
  if ($ok2) { return $dt }

  return $null
}

function Resolve-HeaderName {
  param(
    [Parameter(Mandatory)] [string[]]$Headers,
    [Parameter(Mandatory)] [string]$Preferred
  )
  $pref = ($Preferred + "").Trim()
  foreach ($h in $Headers) {
    if (($h + "").Trim().ToLowerInvariant() -eq $pref.ToLowerInvariant()) { return $h }
  }
  return $Preferred
}

# ========= Exec =========
$stats = [ordered]@{
  downloaded = $false
  csvPath    = $CsvPath
  logPath    = $LogPath
  processed  = 0
  ok         = 0
  skip       = 0
  fail       = 0
  errors     = @()
}

Write-Log "START (HTTP RunNow)"

# 1) Download CSV do Blob
try {
  if (-not $CsvBlobUrl) { throw "CSV_BLOB_URL não definido" }

  Write-Log "Baixando CSV do Blob -> $CsvPath"
  Download-BlobToLocal -BlobUrl $CsvBlobUrl -LocalPath $CsvPath
  $stats.downloaded = $true
  Write-Log "OK download CSV"
}
catch {
  $msg = "FATAL download CSV: $($_.Exception.Message)"
  Write-Log $msg
  $stats.errors += $msg

  Push-OutputBinding -Name Response -Value @{
    StatusCode = 500
    Body       = ($stats | ConvertTo-Json -Depth 7)
    Headers    = @{ "Content-Type" = "application/json" }
  }
  return
}

if (-not (Test-Path $CsvPath)) {
  $msg = "FATAL CSV not found after download: $CsvPath"
  Write-Log $msg
  $stats.errors += $msg

  Push-OutputBinding -Name Response -Value @{
    StatusCode = 500
    Body       = ($stats | ConvertTo-Json -Depth 7)
    Headers    = @{ "Content-Type" = "application/json" }
  }
  return
}

# 2) Token Graph (Refresh Token)
$token = $null
try {
  $token = Get-GraphTokenFromRefreshToken
  Write-Log "OK got delegated token (refresh_token)"

  $jwt = Get-JwtPayload $token
  if ($jwt) {
    Write-Log "TOKEN tid=$($jwt.tid) upn=$($jwt.upn) oid=$($jwt.oid) scp=$($jwt.scp)"
  } else {
    Write-Log "TOKEN payload parse failed"
  }
}
catch {
  $detail = ""
  try { $detail = $_.ErrorDetails.Message } catch {}
  $msg = "FATAL token failed: $($_.Exception.Message) | $detail"
  Write-Log $msg
  $stats.errors += $msg

  Push-OutputBinding -Name Response -Value @{
    StatusCode = 500
    Body       = ($stats | ConvertTo-Json -Depth 9)
    Headers    = @{ "Content-Type" = "application/json" }
  }
  return
}

# 3) Processar CSV
$rows = Import-Csv $CsvPath -Delimiter $Delimiter
if (-not $rows -or $rows.Count -eq 0) {
  Write-Log "FATAL CSV vazio ou inválido"
  Push-OutputBinding -Name Response -Value @{
    StatusCode = 500
    Body       = ($stats | ConvertTo-Json -Depth 9)
    Headers    = @{ "Content-Type" = "application/json" }
  }
  return
}

# Resolve headers reais (evita problema de espaço/case no header)
$headersCsv = @($rows[0].PSObject.Properties.Name)
Write-Log ("CSV_HEADERS: " + ($headersCsv -join " | "))

$UpnColReal  = Resolve-HeaderName -Headers $headersCsv -Preferred $UpnCol
$BdayColReal = Resolve-HeaderName -Headers $headersCsv -Preferred $BdayCol
Write-Log "USING_COLUMNS: UPN='$UpnColReal' BDAY='$BdayColReal'"

$rows | ForEach-Object {
  $stats.processed++

  $upn = Normalize-String $_.($UpnColReal)
  $b   = Normalize-String $_.($BdayColReal)

  if (-not $upn -or -not $b) {
    $stats.skip++
    Write-Log "SKIP [$($stats.processed)] upn='$upn' birthday='$b' (vazio)"
    return
  }

  # ✅ parse que retorna DateTime ou null
  $dt = Parse-DateOrNull $b
  if (-not $dt) {
    $stats.skip++
    Write-Log "SKIP [$($stats.processed)] $upn (data inválida: '$b')"
    return
  }

  # ✅ CHUMBA SEMPRE meio-dia UTC
  $birthdayUtcNoon = [DateTime]::SpecifyKind($dt.Date.AddHours(12), [DateTimeKind]::Utc)
  $birthdayIso = $birthdayUtcNoon.ToString("yyyy-MM-ddTHH:mm:ssZ")

  # Guard rail: nunca enviar MinValue
  if ($birthdayIso -like "0001-01-01*") {
    $stats.skip++
    Write-Log "SKIP_GUARD [$($stats.processed)] $upn (birthday virou MinValue) CSV_BDAY='$b'"
    return
  }

  # Lookup por UPN -> pega ID
  $flt    = "userPrincipalName eq '$upn'"
  $getUri = "https://graph.microsoft.com/v1.0/users?`$filter=$([uri]::EscapeDataString($flt))&`$select=id,userPrincipalName&`$top=1"

  $userId = $null
  try {
    $r = Invoke-RestMethod -Method GET -Uri $getUri -Headers @{ Authorization = "Bearer $token" }
    if ($r.value -and $r.value.Count -gt 0) { $userId = $r.value[0].id }
  } catch {
    $detail = ""
    try { $detail = $_.ErrorDetails.Message } catch {}
    Write-Log "FAIL_LOOKUP $upn -> $($_.Exception.Message) | $detail"
    $stats.fail++
    return
  }

  if (-not $userId) {
    Write-Log "NOTFOUND_LOOKUP $upn"
    $stats.fail++
    return
  }

  Write-Log "FOUND $upn -> id=$userId | CSV_BDAY='$b' | ISO='$birthdayIso'"

  # PATCH por ID
  $uri     = "https://graph.microsoft.com/v1.0/users/$userId"
  $payload = @{ birthday = $birthdayIso } | ConvertTo-Json -Compress

  try {
    Invoke-RestMethod -Method PATCH -Uri $uri `
      -Headers @{ Authorization = "Bearer $token" } `
      -ContentType "application/json" `
      -Body $payload | Out-Null

    $stats.ok++
    Write-Log "OK $upn -> $birthdayIso"
  }
  catch {
    $stats.fail++
    $detail = ""
    try { $detail = $_.ErrorDetails.Message } catch {}
    Write-Log "FAIL_PATCH $upn -> $($_.Exception.Message) | $detail | BODY=$payload"
  }
}

Write-Log "END (HTTP RunNow)"

Push-OutputBinding -Name Response -Value @{
  StatusCode = 200
  Body       = ($stats | ConvertTo-Json -Depth 9)
  Headers    = @{ "Content-Type" = "application/json" }
}
