#Init
$ScanHttpServerFolder = "C:\ScanHttpServer\bin"
$runLoopPath = "$ScanHttpServerFolder\runLoop.ps1"

Start-Transcript -Path C:\VmInit.log

Write-Host Version 1.3.5.2

New-Item -ItemType Directory C:\ScanHttpServer
New-Item -ItemType Directory $ScanHttpServerFolder

$supportHttp = 0

if($args.Count -gt 0){
    if(-Not (Test-Path $ScanHttpServerFolder\vminit.config)){
        New-Item $ScanHttpServerFolder\vminit.config
    }
    $scanHttpServerPackageUrl = $args[2]
    $scanHttpServerPackageUrl = $scanHttpServerPackageUrl.SubString(0, $scanHttpServerPackageUrl.Length - 1)
    Set-Content $ScanHttpServerFolder\vminit.config $scanHttpServerPackageUrl

    $accountName = $args[0]
    $accountName = $accountName.SubString(0, $accountName.Length - 1)
    $accountKey = $args[1]
    $accountKey = $accountKey.SubString(0, $accountKey.Length - 1)
    $supportHttp = $args[3]

    [Environment]::SetEnvironmentVariable("FtsStorageAccountName", $accountName, "Machine")
    [Environment]::SetEnvironmentVariable("FtsStorageAccountKey", $accountKey, "Machine")
    [Environment]::SetEnvironmentVariable("FtsSupportHttp", $supportHttp, "Machine")
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