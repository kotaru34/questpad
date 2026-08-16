param(
    [string]$Adb = "adb.exe",
    [string]$Serial = ""
)

$exe = Join-Path $PSScriptRoot "..\dist\QuestPad-Diagnostic-win64.exe"
if (!(Test-Path $exe)) { throw "QuestPad diagnostic executable not found: $exe" }

$args = @("--adb", $Adb)
if ($Serial) { $args += @("--serial", $Serial) }
& $exe @args
exit $LASTEXITCODE
