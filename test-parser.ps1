param([string]$PluginPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $root 'Mortal_Data\Managed'
$pluginPath = if ($PluginPath) { $PluginPath } else { Join-Path $root 'BepInEx\plugins\LOMDailyAutomation\LOMDailyAutomation.dll' }

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $resolveArgs)
    $name = ([System.Reflection.AssemblyName]$resolveArgs.Name).Name + '.dll'
    foreach ($dir in @(
        (Join-Path $managed ''),
        (Join-Path $root 'BepInEx\core'),
        (Join-Path $root 'BepInEx\plugins\LOMDailyAutomation')
    )) {
        $candidate = Join-Path $dir $name
        if (Test-Path -LiteralPath $candidate) {
            try { return [System.Reflection.Assembly]::LoadFrom($candidate) } catch { }
        }
    }
    return $null
})

$core = [System.Reflection.Assembly]::LoadFrom((Join-Path $managed 'Mortal.Core.dll'))
$plugin = [System.Reflection.Assembly]::LoadFrom($pluginPath)
$pluginType = $plugin.GetType('LOMDailyAutomation.DailyAutomationPlugin', $true)
$method = $pluginType.GetMethod('TryParsePosition', [System.Reflection.BindingFlags]'NonPublic,Static')
$enumType = $core.GetType('Mortal.Core.PositionType', $true)
$zero = [Enum]::Parse($enumType, '無')

$cases = @{
    '正心堂' = '正心堂'
    '校场' = '校場'
    '炼丹房' = '煉丹房'
    '锻冶场' = '鍛冶場'
    '后山' = '後山'
    '讲经堂' = '講經堂'
    'Kitchen' = '伙房'
    '2' = '校場'
    '8' = '講經堂'
}

foreach ($case in $cases.GetEnumerator()) {
    $arguments = @($case.Key, $zero)
    $ok = [bool]$method.Invoke($null, $arguments)
    if (-not $ok -or $arguments[1].ToString() -ne $case.Value) {
        throw "解析失败: $($case.Key) => ok=$ok value=$($arguments[1])"
    }
}

$invalid = @('NoSuchPlace', '')
foreach ($value in $invalid) {
    $arguments = @($value, $zero)
    if ([bool]$method.Invoke($null, $arguments)) {
        throw "无效地点被接受: $value"
    }
}

Write-Output "parser-smoke: $($cases.Count) valid + $($invalid.Count) invalid cases passed"

$normalizeMethod = $pluginType.GetMethod('NormalizeInnerAction', [System.Reflection.BindingFlags]'NonPublic,Static')
$normalizeCases = @{
    'incense+work+incense' = '工作'
    'pickwood+work+pickwood' = '工作'
    'meditate+free+meditate' = '自由'
    'training+free+training' = '自由'
    'talk+talk+brother3' = ''
    'move+闹市+0+0' = ''
    '~x' = ''
    '工作' = '工作'
    'work' = '工作'
    '自由' = '自由'
    'free' = '自由'
    '修炼' = '自由'
    '嘴攻：「因为你说的，乃是真正的无知妄言啊。」' = ''
}
foreach ($case in $normalizeCases.GetEnumerator()) {
    $result = [string]$normalizeMethod.Invoke($null, @($case.Key))
    if ($result -ne $case.Value) {
        throw "类别解析失败: [$($case.Key)] => [$result] 期望 [$($case.Value)]"
    }
}
Write-Output "normalize-smoke: $($normalizeCases.Count) cases passed"
