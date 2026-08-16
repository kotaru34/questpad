param(
    [Parameter(Mandatory=$true)][string]$Apk,
    [string]$Adb = "adb.exe",
    [string]$Serial = ""
)

$args = @()
if ($Serial) { $args += @("-s", $Serial) }
$args += @("install", "-r", $Apk)
& $Adb @args
exit $LASTEXITCODE
