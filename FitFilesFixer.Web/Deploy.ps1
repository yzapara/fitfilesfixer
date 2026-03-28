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
$projectFile = Join-Path $projectDir "FitFilesFixer.csproj"
dotnet publish "$projectFile" -c Release -o "$publishDir"
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

# --- DATABASE MIGRATION ---
Write-Host "4/5: Running remote DB migration (saved_file_name column)..."
$dbMigrationScript = @"
    DB=$remotePath/data/fitfiles.db
    if [ ! -f "`$DB" ]; then echo 'DB not found, skipping migration'; exit 0; fi
    hascol=`$(sqlite3 "`$DB" 'PRAGMA table_info(requests);' | cut -d'|' -f2 | grep -x 'saved_file_name' | wc -l)`
    if [ `$hascol -eq 1 ]; then
        echo 'Column already exists; skipping one-time cleanup.'
    else
        sqlite3 "`$DB" 'ALTER TABLE requests ADD COLUMN saved_file_name TEXT;' && echo 'Column added'
        echo 'Running one-time cleanup of stale /tmp/fiteditor files...'
        TMPDIR='/tmp/fiteditor'
        mkdir -p "`$TMPDIR"
        find "`$TMPDIR" -maxdepth 1 -type f -mtime +7 -print -delete || true
        find "`$TMPDIR" -maxdepth 1 -type d -mtime +7 -print -exec rm -rf {} \; || true
    fi
"@
$dbMigrationScript.Trim() | ssh -i $sshKey "$remoteUser@$remoteHost" bash
if ($LASTEXITCODE -ne 0) { Write-Error "DB migration failed"; exit 1 }

# --- RESTART SERVICE ---
Write-Host "6/6: Restarting service..."
ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl restart $serviceName && sudo systemctl reload nginx"
if ($LASTEXITCODE -ne 0) { Write-Error "Service restart failed"; exit 1 }

# --- SANITY CHECK ---
Write-Host "7/6: Sanity check..."
Start-Sleep -Seconds 2
$appStatus   = ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl is-active $serviceName"
$nginxStatus = ssh -i $sshKey "$remoteUser@$remoteHost" "sudo systemctl is-active nginx"

if ($appStatus -ne "active") { Write-Warning "Service '$serviceName' is not active! Status: $appStatus" }
else { Write-Host "  $serviceName : $appStatus" }

if ($nginxStatus -ne "active") { Write-Warning "nginx is not active! Status: $nginxStatus" }
else { Write-Host "  nginx        : $nginxStatus" }

Write-Host ""
Write-Host "Deployment complete! https://fitfixer.duckdns.org"