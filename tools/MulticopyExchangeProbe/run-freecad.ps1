param([Parameter(Mandatory=$true)][string]$Script,[string[]]$Arguments=@(),
 [Parameter(Mandatory=$true)][string]$OutputPrefix,[int]$TimeoutSeconds=60)
$ErrorActionPreference='Stop'
$taskStart=[Diagnostics.ProcessStartInfo]::new()
$taskStart.FileName='C:/Program Files/FreeCAD 1.1/bin/python.exe'
$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
$taskStart.ArgumentList.Add([IO.Path]::GetFullPath($Script))
foreach($taskArgument in $Arguments){$taskStart.ArgumentList.Add($taskArgument)}
$taskStart.Environment['QT_QPA_PLATFORM']='offscreen'
$taskRun=[Diagnostics.Process]::Start($taskStart)
$taskOut=$taskRun.StandardOutput.ReadToEndAsync();$taskError=$taskRun.StandardError.ReadToEndAsync()
try{
 $taskDeadline=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
 while(!$taskRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $taskDeadline){$taskRun.Kill();throw 'FreeCAD observation timeout'}}
 [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPrefix+'.stdout.log'),$taskOut.GetAwaiter().GetResult())
 [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPrefix+'.stderr.log'),$taskError.GetAwaiter().GetResult())
 Write-Output ('FreeCAD exit '+$taskRun.ExitCode)
 if($taskRun.ExitCode -ne 0){throw 'FreeCAD observation failed; retained output logs contain the exact error'}
}finally{$taskRun.Dispose()}
