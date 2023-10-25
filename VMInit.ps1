#Init
$ScanHttpServerFolder = "C:\ScanHttpServer\bin"
$runLoopPath = "$ScanHttpServerFolder\runLoop.ps1"

Start-Transcript -Path C:\VmInit.log

New-Item -ItemType Directory C:\ScanHttpServer
New-Item -ItemType Directory $ScanHttpServerFolder

$supportHttp = 0

if($args.Count -gt 0){
    if(-Not (Test-Path $ScanHttpServerFolder\vminit.config)){
        New-Item $ScanHttpServerFolder\vminit.config
    }
    $scanHttpServerPackageUrl = $args[4]
    $scanHttpServerPackageUrl = $scanHttpServerPackageUrl.SubString(0, $scanHttpServerPackageUrl.Length - 1)
    Set-Content $ScanHttpServerFolder\vminit.config $scanHttpServerPackageUrl

    $tenantId = $args[0]
    $tenantId = $tenantId.SubString(0, $tenantId.Length - 1)
    $clientId = $args[1]
    $clientId = $clientId.SubString(0, $clientId.Length - 1)
    $clientSecret = $args[2]
    $clientSecret = $clientSecret.SubString(0, $clientSecret.Length - 1)
    $appConfigurationConnString = $args[3]
    $appConfigurationConnString = $appConfigurationConnString.SubString(0, $appConfigurationConnString.Length - 1)
    $supportHttp = $args[5]

    [Environment]::SetEnvironmentVariable("AZURE_TENANT_ID", $tenantId, "Machine")
    [Environment]::SetEnvironmentVariable("AZURE_CLIENT_ID", $clientId, "Machine")
    [Environment]::SetEnvironmentVariable("AZURE_CLIENT_SECRET", $clientSecret, "Machine")
    [Environment]::SetEnvironmentVariable("APP_CONFIGURATION_CONN_STRING", $appConfigurationConnString, "Machine")
}

$ScanHttpServerBinZipUrl = Get-Content $ScanHttpServerFolder\vminit.config

# Download Http Server bin files
Invoke-WebRequest $ScanHttpServerBinZipUrl -OutFile $ScanHttpServerFolder\ScanHttpServer.zip
Expand-Archive $ScanHttpServerFolder\ScanHttpServer.zip -DestinationPath $ScanHttpServerFolder\ -Force

Set-Location $ScanHttpServerFolder

Write-Host Scheduling task for startup

&schtasks /create /tn StartScanHttpServer /sc onstart /tr "powershell.exe C:\ScanHttpServer\bin\runLoop.ps1"  /NP /DELAY 0001:00 /RU SYSTEM

Write-Host Creating and adding certificate

$cert = New-SelfSignedCertificate -DnsName ScanServerCert -CertStoreLocation "Cert:\LocalMachine\My"
$thumb = $cert.Thumbprint
$appGuid = '{'+[guid]::NewGuid().ToString()+'}'

Write-Host successfully created new certificate $cert

netsh http delete sslcert ipport=0.0.0.0:443
netsh http add sslcert ipport=0.0.0.0:443 appid=$appGuid certhash="$thumb"

Write-Host Adding firewall rules

New-NetFirewallRule -DisplayName "ServerFunctionComunication443In" -Direction Inbound -LocalPort 443 -Protocol TCP -Action Allow
New-NetFirewallRule -DisplayName "ServerFunctionComunication443Out" -Direction Outbound -LocalPort 443 -Protocol TCP -Action Allow
if($supportHttp -gt 0)
{
    New-NetFirewallRule -DisplayName "ServerFunctionComunication80In" -Direction Inbound -LocalPort 80 -Protocol TCP -Action Allow
    New-NetFirewallRule -DisplayName "ServerFunctionComunication80Out" -Direction Outbound -LocalPort 80 -Protocol TCP -Action Allow
}
else 
{
    New-NetFirewallRule -DisplayName "ServerFunctionComunication80In" -Direction Inbound -LocalPort 80 -Protocol TCP -Action Deny
    New-NetFirewallRule -DisplayName "ServerFunctionComunication80Out" -Direction Outbound -LocalPort 80 -Protocol TCP -Action Deny
}


#Updating antivirus Signatures
Write-Host Updating Signatures for the antivirus
& "C:\Program Files\Windows Defender\MpCmdRun.exe" -SignatureUpdate
#Running the App
Write-Host Starting Run-Loop
start-process powershell -verb runas -ArgumentList $runLoopPath

Stop-Transcript