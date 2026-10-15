param([switch]$NoInstall, [switch]$Package, [string]$BepInExSource)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $root 'LOMDailyAutomation-src\DailyAutomationPlugin.cs'),
    (Join-Path $root 'LOMDailyAutomation-src\PanelAutomation.cs'),
    (Join-Path $root 'LOMDailyAutomation-src\CombatControls.cs')
)
$outputDir = Join-Path $root 'LOMDailyAutomation-src\bin'
$installedDir = Join-Path $root 'BepInEx\plugins\LOMDailyAutomation'
$csc = Join-Path ${env:ProgramFiles} 'dotnet\sdk\10.0.401\Roslyn\bincore\csc.dll'
$managed = Join-Path $root 'Mortal_Data\Managed'
$bepCore = Join-Path $root 'BepInEx\core'

if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 Roslyn csc.dll: $csc" }
New-Item -ItemType Directory -Force -Path $outputDir, $installedDir | Out-Null

$references = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'netstandard.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $bepCore '0Harmony.dll'),
    (Join-Path $bepCore 'BepInEx.Core.dll'),
    (Join-Path $bepCore 'BepInEx.Unity.Mono.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'Fungus.dll'),
    (Join-Path $managed 'Mortal.Battle.dll'),
    (Join-Path $managed 'Mortal.Combat.dll'),
    (Join-Path $managed 'Mortal.Core.dll'),
    (Join-Path $managed 'Mortal.Free.dll'),
    (Join-Path $managed 'Mortal.Story.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "缺少编译引用: $reference" }
}

$args = @($csc, '/noconfig', '/nostdlib+', '/target:library', '/langversion:7.3', '/optimize+', "/out:$outputDir\LOMDailyAutomation.dll") + $sources
foreach ($reference in $references) { $args += "/r:$reference" }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "csc 编译失败，退出码 $LASTEXITCODE" }

if ($NoInstall) {
    Write-Output "已构建（未安装）: $outputDir\LOMDailyAutomation.dll"
    if (-not $Package) { exit 0 }
}

if (-not $NoInstall) {
    Copy-Item -LiteralPath "$outputDir\LOMDailyAutomation.dll" -Destination "$installedDir\LOMDailyAutomation.dll" -Force
    Write-Output "已构建并安装: $installedDir\LOMDailyAutomation.dll"
}

# -------------------------------------------------------------------- 打包
# 生成「解压即用」发布包：自带 BepInEx 运行时，用户不必先去别处装 BepInEx。
# 包内 = winhttp.dll + doorstop_config.ini + .doorstop_version + changelog.txt
#        + BepInEx\core\* + BepInEx\plugins\LOMDailyAutomation\LOMDailyAutomation.dll
#        + 一键安装器 + 安装说明 + 第三方许可

if ($Package) {
    $repo = $PSScriptRoot
    $market = [regex]::Match((Get-Content (Join-Path $repo "DailyAutomationPlugin.cs") -Raw), 'PluginVersion\s*=\s*"([^"]+)"')
    if (-not $market.Success) { throw "无法从 DailyAutomationPlugin.cs 里读出 PluginVersion。" }
    $version = $market.Groups[1].Value

    if (-not $BepInExSource) { $BepInExSource = $root }
    if (-not (Test-Path $BepInExSource)) { throw "BepInEx 来源目录不存在: $BepInExSource" }

    # 白名单，不用「排除法」：游戏目录的 BepInEx\plugins / BepInEx\config 里有别人的
    # mod 和配置，混进发布包就是事故。清单对应官方 BepInEx 6 Unity.Mono 发布包。
    $bepRootFiles = @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")
    $bepDirs      = @("BepInEx\core")
    foreach ($f in $bepRootFiles) {
        if (-not (Test-Path (Join-Path $BepInExSource $f))) {
            throw "在 $BepInExSource 里找不到 BepInEx 的 $f，无法打出「解压即用」的包。"
        }
    }
    foreach ($d in $bepDirs) {
        if (-not (Test-Path (Join-Path $BepInExSource $d))) {
            throw "在 $BepInExSource 里找不到 BepInEx 的 $d 目录。"
        }
    }

    $stage = Join-Path $env:TEMP ("lom_daily_package_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    # 1) BepInEx 运行时
    foreach ($f in $bepRootFiles) {
        Copy-Item (Join-Path $BepInExSource $f) -Destination $stage -Force
    }
    foreach ($d in $bepDirs) {
        $dst = Join-Path $stage $d
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item (Join-Path $BepInExSource "$d\*") -Destination $dst -Recurse -Force
    }

    # 2) 本 mod 本体
    $stagePlugin = Join-Path $stage "BepInEx\plugins\LOMDailyAutomation"
    New-Item -ItemType Directory -Path $stagePlugin -Force | Out-Null
    Copy-Item "$outputDir\LOMDailyAutomation.dll" -Destination $stagePlugin -Force
    Copy-Item (Join-Path $repo "LOMDailyAutomation.cfg.example") -Destination $stagePlugin -Force

    # 3) 一键安装器
    $installerDir = Join-Path $repo "tools\installer"
    foreach ($f in @("点我安装.bat", "install.ps1")) {
        $src = Join-Path $installerDir $f
        if (-not (Test-Path $src)) { throw "找不到安装程序文件: $src" }
        Copy-Item $src -Destination $stage -Force
    }

    # 4) 第三方许可（BepInEx 与 Doorstop 都是 LGPL-2.1，随包分发必须带上）
    $licDir = Join-Path $stage "第三方许可"
    New-Item -ItemType Directory -Path $licDir -Force | Out-Null
    foreach ($lic in @("BepInEx-LICENSE.txt", "Doorstop-LICENSE.txt")) {
        $src = Join-Path $repo ("third_party\" + $lic)
        if (Test-Path $src) { Copy-Item $src -Destination $licDir -Force }
    }

    $bepVer = "6.x"
    try {
        $vi = (Get-Item (Join-Path $BepInExSource "BepInEx\core\BepInEx.Core.dll")).VersionInfo
        foreach ($cand in @($vi.ProductVersion, $vi.FileVersion)) {
            if ($cand) { $bepVer = ($cand -split "\+")[0]; break }
        }
    } catch { }
    $doorstopVer = "未知"
    try { $doorstopVer = (Get-Content (Join-Path $BepInExSource ".doorstop_version") -Raw).Trim() } catch { }

    # 5) 安装说明
    $notice = @"
活侠传日常自动化 $version
《活侠传》自由行动日常自动化：F8 一按，日常自己跑

===============================================================
【安装】推荐做法：解压到任意位置，然后双击「点我安装.bat」
===============================================================

1. 把本压缩包解压到**任意位置**（桌面、下载文件夹都行，不用特意找游戏目录）
2. 打开解压出来的文件夹，双击「点我安装.bat」
3. 安装程序会自己找到《活侠传》并复制文件，成功后窗口里会显示
   「安装完成，检查通过」

就这么简单。找不到游戏时它会告诉你怎么办（把游戏文件夹拖到那个 bat 上即可）。

---------------------------------------------------------------
【另一种做法】手工解压到游戏目录
---------------------------------------------------------------

在 Steam 库里右键《活侠传》→ 管理 → 浏览本地文件，打开游戏根目录
（就是 Mortal.exe 所在的那个文件夹），然后：

打开本压缩包，**把里面的文件拖进游戏根目录**（或者全选复制过去）。
注意不要用「解压到 LOMDailyAutomation-v$version」这种会新建子文件夹的方式，
否则文件会落在子文件夹里，游戏读不到。

手工做对之后，游戏根目录下应当有：

    winhttp.dll
    doorstop_config.ini
    .doorstop_version
    BepInEx\core\BepInEx.Unity.Mono.Preloader.dll
    BepInEx\plugins\LOMDailyAutomation\LOMDailyAutomation.dll

【关于 BepInEx】
本包已经自带 BepInEx 运行时（BepInEx $bepVer / Doorstop $doorstopVer，LGPL-2.1），
所以就算你之前什么 mod 都没装过，装完也能直接玩。不需要再去别处下载。

如果你已经装过 BepInEx，直接覆盖一般也没问题。但假如你那份比本包里的新，
可以只处理 BepInEx\plugins 这部分，免得把新版换成旧版。

其它 mod 互不影响：各家插件各自放在 BepInEx\plugins 下自己的文件夹里，
安装新 mod 时继续往 BepInEx\plugins 里放即可。安装程序也只动本 mod 的文件和
BepInEx 自身，不会碰 BepInEx\config 和别人的插件。

===============================================================
【用法】记两个键就够
===============================================================

    F8    开始 / 停止自动化（在自由行动界面按）
    F7    显示 / 隐藏自动化面板

平时只要在自由行动界面按一下 F8，剩下的自动完成：地点照着你面板里的顺序跑，
点进去后的工作/自由、修炼武功、打铁炼丹加点、睡觉过天、大门轮换全都自动处理。
遇到没读过的剧情或需要选择的地方会自动停下来，处理完再按 F8 继续。

【流程顺序】打开面板（F7），在"流程顺序"区用上移/下移/删除整理，用"添加地点"补回来，
随时自动保存。想做哪些日常就留哪些，不想做的删掉即可。

【修炼目标】面板里"功法修炼目标"默认是"医术"，点"切换目标"换成别的属性；
武学面板会自动优先修炼能加这个目标的功法（目标加成在满级那级的医书也会被正确识别）。

【门口轮换】把"大门"加进流程后，每进一次大门自动选下一项（闹市/外堡/茶肆/药材铺/…），
所有出行与随机事件都会轮到。面板上有"大门轮换重置"。

【安装成功了吗】
游戏启动一次后，看 BepInEx\LogOutput.log 里有没有这一行：

    活侠传日常自动化已加载。按 F8 开始/停止，按 F7 显示/隐藏面板。

有就说明装好了。

【设置】
BepInEx\config\com.codex.lom.daily-automation.cfg，首次启动游戏后生成，
每一项都有中文注释；面板里能改的就不用手改文件。

【卸载】
删掉 BepInEx\plugins\LOMDailyAutomation 文件夹即可，不影响存档，其它 mod 也照常。
想彻底清掉 BepInEx，再删 BepInEx 文件夹、winhttp.dll、doorstop_config.ini、.doorstop_version。

项目主页：https://github.com/shiluodeqiou/LOMDailyAutomation
"@
    $notice = ($notice -replace "`r?`n", "`r`n")
    [System.IO.File]::WriteAllText((Join-Path $stage "安装说明.txt"), $notice,
                                   (New-Object System.Text.UTF8Encoding($true)))

    # 6) 第三方组件说明
    $thirdParty = @"
本发布包中包含以下第三方组件，它们不是本项目（活侠传日常自动化）的一部分，
随包分发只是为了让你解压一次就能直接使用。完整许可原文见「第三方许可」文件夹。

  BepInEx（含 Harmony / MonoMod / Mono.Cecil 等）
      版本  : $bepVer
      许可  : GNU LGPL-2.1
      来源  : https://github.com/BepInEx/BepInEx

  UnityDoorstop
      版本  : $doorstopVer
      许可  : GNU LGPL-2.1
      来源  : https://github.com/NeighTools/UnityDoorstop

本项目的源码与许可（MIT）：
  https://github.com/shiluodeqiou/LOMDailyAutomation
"@
    $thirdParty = ($thirdParty -replace "`r?`n", "`r`n")
    [System.IO.File]::WriteAllText((Join-Path $stage "第三方组件与许可.txt"), $thirdParty,
                                   (New-Object System.Text.UTF8Encoding($true)))

    # 7) 压缩。安全校验：确认没有把别人的 mod / 配置带进去
    $strayPlugins = @(Get-ChildItem (Join-Path $stage "BepInEx\plugins") -Directory |
                      Where-Object { $_.Name -ne "LOMDailyAutomation" })
    $strayConfigs = @(Get-ChildItem (Join-Path $stage "BepInEx\config") -File -EA SilentlyContinue)
    if ($strayPlugins.Count -gt 0 -or $strayConfigs.Count -gt 0) {
        throw "暂存目录里混进了别的 mod 或配置，已中止。plugins: $($strayPlugins.Name -join ', ')"
    }

    $fileCount = @(Get-ChildItem $stage -Recurse -File -Force).Count

    $distDir = Join-Path $repo "dist"
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    $zip = Join-Path $distDir ("LOMDailyAutomation-v" + $version + ".zip")
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force

    Write-Output "已打包   : $zip"
    Write-Output ("           共 {0} 个文件，自带 BepInEx {1}，解压到游戏根目录即可" -f $fileCount, $bepVer)
}
