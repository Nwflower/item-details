$ErrorActionPreference = 'Stop'
$testRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$testNamespace = 'SelfTestResults' + [Guid]::NewGuid().ToString('N')
$testSource = '#define ITEMDETAILS_SELFTEST' + [Environment]::NewLine +
    (Get-Content -LiteralPath (Join-Path $testRepo 'Diagnostics/SelfTest.Results.cs') -Raw).Replace(
        'namespace ItemDetails;', "namespace $testNamespace {").Replace(
        'using UnityEngine;', "using $testNamespace.UnityEngine;") + '}' + @'
namespace TEST_NAMESPACE.UnityEngine {
 public static class Application { public static int ExitCode = -1; public static void Quit(int value) { ExitCode = value; } }
}
namespace TEST_NAMESPACE {
 public static class ModHelper {
  public static string Report = "old report";
  public static bool FailWrite;
  public static void WriteModDataFileText(string modId, string path, string text) {
   if (FailWrite) throw new InvalidOperationException("write failed");
   Report = text;
  }
 }
 internal static class Log { internal static void Msg(string text) { } internal static void Error(string scope, object error) { } }
 public static class Smoke {
  public static void Begin() => SelfTest.TestBegin();
  public static void Finish(bool success) => SelfTest.TestFinish(success);
  public static string Report => ModHelper.Report;
  public static int ExitCode => UnityEngine.Application.ExitCode;
  public static void FailWrite() => ModHelper.FailWrite = true;
 }
 internal static partial class SelfTest {
  internal static void TestBegin() => BeginRun("item_details");
  internal static void TestFinish(bool success) => Finish(success, success ? "pass=1 fail=0" : "setup failed");
 }
}
'@.Replace('TEST_NAMESPACE', $testNamespace)
$testTypes = Add-Type -TypeDefinition $testSource -PassThru
$testSmoke = $testTypes | Where-Object Name -eq 'Smoke'
function Invoke-Smoke([string]$name, [object[]]$arguments = @()) {
    $testSmoke.GetMethod($name).Invoke($null, $arguments)
}
function Read-Smoke([string]$name) { $testSmoke.GetProperty($name).GetValue($null) }

Invoke-Smoke 'Begin'
$testFirstRun = Read-Smoke 'Report'
if ($testFirstRun -notmatch 'status=RUNNING' -or $testFirstRun -eq 'old report') { throw 'Startup did not replace the old report.' }
Invoke-Smoke 'Finish' @($false)
if ((Read-Smoke 'ExitCode') -ne 1 -or (Read-Smoke 'Report') -notmatch 'status=FAIL[\s\S]*setup failed') {
    throw 'Setup failure did not produce a fresh failed result and nonzero exit.'
}
Invoke-Smoke 'Begin'
$testSecondRun = Read-Smoke 'Report'
if (($testFirstRun -split "`n")[0] -eq ($testSecondRun -split "`n")[0]) { throw 'Runs did not receive distinct identities.' }
Invoke-Smoke 'Finish' @($true)
if ((Read-Smoke 'ExitCode') -ne 0 -or (Read-Smoke 'Report') -notmatch 'status=PASS') { throw 'Successful run did not exit zero.' }
Invoke-Smoke 'Begin'
Invoke-Smoke 'FailWrite'
Invoke-Smoke 'Finish' @($true)
if ((Read-Smoke 'ExitCode') -ne 1) { throw 'Report-write failure did not fail the run.' }
Write-Output 'PASS: fresh startup/setup/completion reports, distinct run IDs, and failure exit codes.'
