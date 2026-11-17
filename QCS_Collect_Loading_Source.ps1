# QCS startup / Test Result loading investigation package
# Run from any location; original project files are never modified.
$ErrorActionPreference = 'Stop'
$projectRoot = 'C:\QCS\QCS-개발'
$zipPath = Join-Path $projectRoot 'QCS_Loading_Analysis_Source.zip'

if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
    throw "Project directory not found: $projectRoot"
}

# Selected from QCS-tree.txt. Missing files are reported, not fabricated.
$selected = @(
    'QCS.csproj',
    'QCS.slnx',
    'App.xaml', 'App.xaml.cs',
    'MainWindow.xaml', 'MainWindow.xaml.cs',
    'TestResultListView.xaml', 'TestResultListView.xaml.cs',
    'DatabaseManager.cs',
    'StartupUserService.cs',
    'UserSwitchView.xaml', 'UserSwitchView.xaml.cs',
    'Styles\QcsCommonStyles.xaml',
    'Styles\QcsModal.cs',
    'Styles\QcsBatchChoiceDialog.cs'
)

# Source-only supporting code: include project C# and XAML files at root
# to capture referenced classes without copying binary/data assets.
$rootSource = Get-ChildItem -LiteralPath $projectRoot -File |
    Where-Object { $_.Extension -in @('.cs', '.xaml') } |
    ForEach-Object { $_.Name }
$selected = @($selected + $rootSource | Select-Object -Unique)

# Also include source code in Analysis and Properties if present.
foreach ($sub in @('Analysis', 'Properties')) {
    $dir = Join-Path $projectRoot $sub
    if (Test-Path -LiteralPath $dir -PathType Container) {
        $selected += Get-ChildItem -LiteralPath $dir -File -Recurse |
            Where-Object { $_.Extension -in @('.cs', '.xaml') } |
            ForEach-Object { $_.FullName.Substring($projectRoot.Length + 1) }
    }
}
$selected = @($selected | Select-Object -Unique)

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("QCS_Source_" + [guid]::NewGuid().ToString('N'))
$copied = 0
$missing = @()
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    foreach ($rel in $selected) {
        $src = Join-Path $projectRoot $rel
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            $missing += $rel
            continue
        }
        $dest = Join-Path $temp $rel
        $parent = Split-Path -Path $dest -Parent
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath $src -Destination $dest
        $copied++
    }
    if ($copied -eq 0) { throw 'No files were found to package.' }
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $temp '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Created: $zipPath"
    Write-Host "Files included: $copied"
    if ($missing.Count -gt 0) {
        Write-Warning ("Missing files: " + ($missing -join ', '))
    }
    Write-Host 'Excluded: DB files, Database.json, credentials, bin/obj, Git metadata, Excel/PDF files.'
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
