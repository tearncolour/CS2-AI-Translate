# 发布 / 更新到 Paradox Mods。
# 用法（在 PowerShell 中）：
#   .\Tools\Publish.ps1                     # 使用游戏已登录的 Paradox 账号（自动登录）
#   .\Tools\Publish.ps1 -Email you@x.com    # 自动登录失败时，用邮箱 + 密码登录（密码会提示输入，不保存）
#   .\Tools\Publish.ps1 -Command Update     # 只更新介绍/图片，不上传新版本
param(
    [string]$Email,
    [ValidateSet('Auto', 'Publish', 'NewVersion', 'Update')]
    [string]$Command = 'Auto',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$config = Join-Path $root 'Properties\PublishConfiguration.xml'
$publisher = Join-Path $GamePath 'Cities2_Data\Content\Game\.ModdingToolchain\ModPublisher\ModPublisher.exe'
if (-not (Test-Path $publisher)) { throw "找不到 ModPublisher：$publisher" }

# 首次发布用 Publish；配置里已有 ModId 时上传新版本。
if ($Command -eq 'Auto') {
    $modId = ([xml](Get-Content $config -Raw -Encoding UTF8)).Publish.ModId.Value
    $Command = if ([string]::IsNullOrWhiteSpace($modId)) { 'Publish' } else { 'NewVersion' }
}

# 编译并整理上传内容（只包含 mod 本体）。
Write-Host "Building..."
dotnet build (Join-Path $root 'AITranslate.csproj') -c Release -p:DeployToGame=false | Out-Host
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
$content = Join-Path $root 'bin\PublishContent'
if (Test-Path $content) { Remove-Item $content -Recurse -Force }
New-Item -ItemType Directory $content | Out-Null
Copy-Item (Join-Path $root 'bin\Release\AITranslate.dll') $content
Copy-Item (Join-Path $root 'bin\Release\AITranslate.pdb') $content -ErrorAction SilentlyContinue

$publishArgs = @($Command, $config, '-c', $content, '-v')
if ($Email) {
    $secure = Read-Host "Paradox 账号密码" -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    $publishArgs += @('--noAutoLogin', '-e', $Email, '-p', $plain)
}

Write-Host "Running ModPublisher $Command ..."
Push-Location $root
try {
    & $publisher @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "ModPublisher 失败（退出码 $LASTEXITCODE）" }
}
finally {
    Pop-Location
}
Write-Host "完成。首次发布后请把平台分配的 Mod ID 填到 Properties\PublishConfiguration.xml 的 <ModId Value=`"`" /> 中，以后更新会自动用 NewVersion。"
