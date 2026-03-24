# Define variables for clarity and easy maintenance
# $PSScriptRoot is the folder containing this script — works regardless of where
# the script is invoked from, as long as the script lives next to Program.cs
$projectDir = $PSScriptRoot
$dataDir    = "$projectDir\data"
$dbPath     = "$dataDir\fitfiles.db"

# --- Stage 1: Ensure the local data directory exists ---
Write-Host "1/2: Creating local data directory '$dataDir'..."

if (-Not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir | Out-Null
    Write-Host "     Directory created."
} else {
    Write-Host "     Directory already exists, skipping."
}

# --- Stage 2: Create schema via a temporary dotnet console project ---
# NOTE: No sqlite3.exe or extra global tools required — only 'dotnet' which is
#       already installed as part of your development environment.
Write-Host "2/2: Initialising SQLite database at '$dbPath'..."

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "fitfiles_db_init"

# Clean up any leftover temp project from a previous run
if (Test-Path $tmpDir) {
    Remove-Item $tmpDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tmpDir | Out-Null

# Write the .csproj
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
  </ItemGroup>
</Project>
"@ | Set-Content "$tmpDir\init.csproj" -Encoding UTF8

# NOTE: The C# connection string uses string concatenation ("Data Source=" + args[0])
#       instead of interpolation to avoid any PowerShell escape-character conflicts
#       when writing this here-string to disk.
@'
using Microsoft.Data.Sqlite;

var connStr = "Data Source=" + args[0];
using var conn = new SqliteConnection(connStr);
conn.Open();

var cmd = conn.CreateCommand();
cmd.CommandText = @"
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
";
cmd.ExecuteNonQuery();
Console.WriteLine("Schema created successfully.");
'@ | Set-Content "$tmpDir\Program.cs" -Encoding UTF8

# Run the temp project, passing the DB path as an argument
dotnet run --project $tmpDir -- $dbPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Database initialisation failed."
    exit 1
}

# Clean up the temp project
Remove-Item $tmpDir -Recurse -Force

Write-Host ""
Write-Host "Local database initialised successfully at '$dbPath'"
Write-Host ""
Write-Host "Add this to your appsettings.Development.json:"
Write-Host "  ""ConnectionStrings"": { ""Sqlite"": ""Data Source=$dbPath"" }"