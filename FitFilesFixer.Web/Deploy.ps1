# ========================================
# PRO DEPLOY SCRIPT WITH RSYNC (FAST & CLEAN)
# ========================================

# --- CONFIG ---
$projectDir  = $PSScriptRoot
$publishDir  = Join-Path $PSScriptRoot "publish"
$wslSshKey   = "/root/ubuntu_rsa.pem"
$sshKey      = Join-Path $PSScriptRoot "ubuntu_rsa.pem"

$remoteUser  = "ubuntu"
$remoteHost  = "3.72.38.57"
$remotePath  = "/home/ubuntu/app"
$serviceName = "fitfiles"

# --- PUBLISH ---
Write-Host "1/5: Publishing..."
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
dotnet publish "$projectDir" -c Release -o "$publishDir"
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

# --- CREATE REMOTE FOLDERS ---
Write-Host "2/5: Ensuring remote directories..."
ssh -i $sshKey "$remoteUser@$remoteHost" "mkdir -p $remotePath && mkdir -p $remotePath/data && chown ubuntu:ubuntu $remotePath/data"
if ($LASTEXITCODE -ne 0) { Write-Error "Remote directory setup failed"; exit 1 }

# --- RSYNC DEPLOY ---
Write-Host "3/5: Uploading via rsync..."
$wslPublishDir = (wsl wslpath $publishDir.Replace('\', '\\'))

wsl rsync -avz --delete `
    --exclude 'data/' `
    -e "ssh -i $wslSshKey -o StrictHostKeyChecking=no" `
    "$wslPublishDir/" `
    "${remoteUser}@${remoteHost}:$remotePath"

if ($LASTEXITCODE -ne 0) { Write-Error "rsync failed"; exit 1 }

# --- RESTART SERVICE ---
Write-Host "4/5: Restarting service..."
ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl restart $serviceName && sudo systemctl reload nginx"
if ($LASTEXITCODE -ne 0) { Write-Error "Service restart failed"; exit 1 }

# --- SANITY CHECK ---
Write-Host "5/5: Sanity check..."
Start-Sleep -Seconds 2
$appStatus   = ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl is-active $serviceName"
$nginxStatus = ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl is-active nginx"

if ($appStatus -ne "active") { Write-Warning "Service '$serviceName' is not active! Status: $appStatus" }
else { Write-Host "  $serviceName : $appStatus" }

if ($nginxStatus -ne "active") { Write-Warning "nginx is not active! Status: $nginxStatus" }
else { Write-Host "  nginx        : $nginxStatus" }

Write-Host ""
Write-Host "Deployment complete! https://fitfixer.duckdns.org"