# UpdateDb-AddSavedFileName.ps1
# Safe SQLite migration: add saved_file_name column to requests table if missing.

param(
    [string]$DatabasePath = "data\fitfiles.db"
)

if (-Not (Test-Path $DatabasePath)) {
    Write-Error "Database file not found: $DatabasePath"
    exit 1
}

Write-Host "Checking requests table schema in $DatabasePath..."

$columnExists = & sqlite3 $DatabasePath "PRAGMA table_info(requests);" | ForEach-Object {
    $parts = $_ -split '\|' ; $parts[1]
} | Where-Object { $_ -eq 'saved_file_name' }

if ($columnExists) {
    Write-Host "Column 'saved_file_name' already exists; no changes required."
    exit 0
}

Write-Host "Adding column 'saved_file_name'..."

& sqlite3 $DatabasePath "ALTER TABLE requests ADD COLUMN saved_file_name TEXT;"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to add column 'saved_file_name'."
    exit 1
}

Write-Host "Migration complete: saved_file_name column added."
Write-Host "Existing rows are preserved and new rows may set SavedFileName via application."