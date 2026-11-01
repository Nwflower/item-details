$ErrorActionPreference = 'Stop'
$testRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$testNamespace = 'LocalizationTests' + [Guid]::NewGuid().ToString('N')

# Compile production Loc.cs and the plural helper it calls. Only external locale,
# clock and file access are adapted; formatting, table parsing and fallback
# execute the production code.
$sources = @('src/Infrastructure/Loc.cs', 'src/Domain/Plural.Pure.cs') | ForEach-Object {
    (Get-Content -LiteralPath (Join-Path $testRepo $_) -Raw).Replace(
        'namespace ItemDetails;', '').Replace(
        'using UnityEngine;', "using $testNamespace.UnityEngine;")
}
$usings = [System.Collections.Generic.List[string]]::new()
$bodies = foreach ($source in $sources) {
    $lines = $source -split '\r?\n'
    foreach ($line in $lines) {
        if ($line -match '^using ' -and -not $usings.Contains($line)) { $usings.Add($line) }
    }
    ($lines | Where-Object { $_ -notmatch '^using ' }) -join "`n"
}
$testSource = '#nullable enable' + [Environment]::NewLine +
    ($usings -join [Environment]::NewLine) + [Environment]::NewLine +
    "namespace $testNamespace {" + [Environment]::NewLine +
    ($bodies -join [Environment]::NewLine) + [Environment]::NewLine + '}' + @'
namespace TEST_NAMESPACE.UnityEngine { public static class Time { public static float realtimeSinceStartup => 0f; } }
namespace TEST_NAMESPACE {
public static class LocHelper { public static string GetCurrentLocaleCode() => "zh"; }
public static class ModHelper { public static string Raw = ""; public static string ReadModFileText(string modId, string path) => Raw; }
internal static class Log { internal static void Warn(string scope, object message) { } }
public static class LocCases {
 public static string Run(string table, string english) {
  ModHelper.Raw = table;
  Loc.ResetCache();
  return Loc.T("probe", english, 7);
 }
}
}
'@.Replace('TEST_NAMESPACE', $testNamespace)

$testTypes = Add-Type -TypeDefinition $testSource -PassThru
$testCasesType = $testTypes | Where-Object { $_.Name -eq 'LocCases' }
foreach ($testCase in @(
    @{ Name = 'valid translation'; Table = 'probe,Translated {0:D2}'; English = 'English {0}'; Expected = 'Translated 07' },
    @{ Name = 'missing translation'; Table = 'other,Other'; English = 'English {0}'; Expected = 'English 7' },
    @{ Name = 'out of range translated argument'; Table = 'probe,Broken {1}'; English = 'English {0}'; Expected = 'English 7' },
    @{ Name = 'unclosed translated item'; Table = 'probe,Broken {0'; English = 'English {0}'; Expected = 'English 7' },
    @{ Name = 'invalid English fallback remains readable'; Table = 'probe,Broken {1}'; English = 'English {2}'; Expected = 'English {2}' },
    @{ Name = 'plural form in English fallback'; Table = 'other,Other'; English = '{0} {0|day|days}'; Expected = '7 days' },
    @{ Name = 'plural form in translation'; Table = 'probe,已处理 {0} {0|件|件数}'; English = '{0} {0|item|items}'; Expected = '已处理 7 件' }
)) {
    $testResult = $testCasesType.GetMethod('Run').Invoke($null, @($testCase.Table, $testCase.English))
    if ($testResult -ne $testCase.Expected) {
        throw ($testCase.Name + ': expected [' + $testCase.Expected + '], got [' + $testResult + ']')
    }
    Write-Output ('PASS ' + $testCase.Name)
}
