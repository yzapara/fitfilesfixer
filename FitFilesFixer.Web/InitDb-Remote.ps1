# Define variables for clarity and easy maintenance
$sshKey     = Join-Path $PSScriptRoot "ubuntu_rsa.pem"
$remoteUser = "ubuntu"
$remoteHost = "3.72.38.57"
$dbDir      = "~/app/data"
$dbPath     = "$dbDir/fitfiles.db"

# --- Stage 1: Ensure sqlite3 is installed and data directory exists ---
Write-Host "1/3: Installing sqlite3 on remote host (skipped if already present)..."

ssh -i $sshKey "${remoteUser}@${remoteHost}" "sudo apt-get install -y sqlite3"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install sqlite3 on remote host."
    exit 1
}

Write-Host "2/3: Creating data directory '$dbDir' on remote host..."

ssh -i $sshKey "${remoteUser}@${remoteHost}" "mkdir -p $dbDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create data directory on remote host."
    exit 1
}

# --- Stage 2: Create the SQLite database and schema ---
Write-Host "3/3: Initialising SQLite database at '$dbPath' on remote host..."
$createSchemaCommand = @"
sqlite3 $dbPath <<'EOF'
CREATE TABLE IF NOT EXISTS requests (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp           TEXT    NOT NULL,
    ip                  TEXT,
    country             TEXT,
    city                TEXT,
    file_name           TEXT,
    file_size_kb        INTEGER,
    total_points        INTEGER,
    fixed_points        INTEGER,
    dropped_timestamp   INTEGER,
    dropped_duplicate   INTEGER,
    dropped_corrupt     INTEGER,
    processing_ms       INTEGER,
    success             INTEGER NOT NULL DEFAULT 1,
    error_message       TEXT
);
EOF
"@

ssh -i $sshKey "${remoteUser}@${remoteHost}" $createSchemaCommand

if ($LASTEXITCODE -ne 0) {
    Write-Error "Database initialisation failed. Ensure sqlite3 is installed: sudo apt-get install -y sqlite3"
    exit 1
}

Write-Host "Database initialised successfully at ${remoteHost}:${dbPath}"