# Define variables for clarity and easy maintenance
$scriptDir = $PSScriptRoot                   # folder where Deploy.ps1 lives (SSH key is here)
$projectDir = $PSCommandPath | Split-Path -Parent  # same as $PSScriptRoot; script runs from project folder
$publishDir = Join-Path $projectDir "publish"
$sshKey = Join-Path $scriptDir "ubuntu_rsa.pem"
$remoteUser = "ubuntu"
$remoteHost = "3.72.38.57"
$remotePath = "~/app/"
$serviceName = "fitfiles.service" 

# --- Stage 1: Publish the .NET application ---
Write-Host "1/3: Publishing the .NET application..."
dotnet publish -c Release -o publish

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

# --- Stage 2: Securely copy the published files to the remote server using SCP ---
Write-Host "2/3: Copying files to ${remoteUser}@${remoteHost}:${remotePath} ..."
scp -i $sshKey -r "$publishDir\*" "${remoteUser}@${remoteHost}:${remotePath}"

if ($LASTEXITCODE -ne 0) {
    Write-Error "SCP failed. Ensure the key path is correct and the remote host is reachable."
    exit 1
}

# --- Stage 3: Restart the service on the remote Ubuntu server using SSH ---
Write-Host "3/3: Restarting service '$serviceName' on remote host..."
$restartCommand = "sudo systemctl restart $serviceName"

# Use SSH to execute the restart command remotely
ssh -i $sshKey "${remoteUser}@${remoteHost}" $restartCommand

if ($LASTEXITCODE -ne 0) {
    Write-Error "Remote service restart failed. Check the service name and sudo permissions."
    # WARNING: If the script fails here, the new files are on the server, but the old app is still running.
}

Write-Host "Deployment complete. New changes are now live!"