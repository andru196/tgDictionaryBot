# Прогон всех тестов.
#
# `dotnet test` на .NET 10 SDK требует явного включения нового раннера, а xunit.v3
# собирает тестовые проекты как самостоятельные исполняемые файлы — запустить их
# напрямую и проще, и надёжнее.

$ErrorActionPreference = "Stop"
$projects = Get-ChildItem -Path "$PSScriptRoot\tests" -Filter *.csproj -Recurse

dotnet build "$PSScriptRoot" --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$failed = @()
foreach ($project in $projects) {
    $output = dotnet run --project $project.FullName --no-build 2>&1
    $summary = $output | Where-Object { $_ -match 'Total:' }

    if ($summary) { $summary } else { $output | Select-Object -Last 15 }
    if ($LASTEXITCODE -ne 0) { $failed += $project.BaseName }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Падения: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Все тесты прошли." -ForegroundColor Green
