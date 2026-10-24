param([string]$RunDirectory='artifacts/comprehensive/run-final',[string]$Dataset='artifacts/data/query-20261023-134809',[string[]]$Engines=@('Baseline','Optimized','SQLite'))
$ErrorActionPreference='Stop'
Set-Location -LiteralPath (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$physical=Join-Path (Resolve-Path $RunDirectory).Path 'physical'
New-Item -ItemType Directory -Path $physical -Force | Out-Null
$records=@()
foreach($engine in $Engines) {
    $stream=Join-Path $physical ($engine+'-counters.jsonl')
    $ready=Join-Path $physical ($engine+'-ready.flag')
    if(Test-Path -LiteralPath $ready){Remove-Item -LiteralPath $ready}
    $job=Start-Job -ArgumentList $stream,$ready -ScriptBlock {
        param($stream,$ready)
        $ErrorActionPreference='Stop'
        $writer=[System.IO.StreamWriter]::new($stream,$false,[System.Text.UTF8Encoding]::new($true))
        try {
            $paths=@('\PhysicalDisk(*)\% Idle Time','\PhysicalDisk(*)\Disk Read Bytes/sec','\PhysicalDisk(*)\Disk Write Bytes/sec','\PhysicalDisk(*)\Disk Transfers/sec','\PhysicalDisk(*)\Current Disk Queue Length','\PhysicalDisk(*)\Avg. Disk sec/Transfer')
            Get-Counter -Counter $paths -SampleInterval 1 -Continuous | ForEach-Object {
                $stamp=$_.Timestamp.ToUniversalTime().ToString('o')
                foreach($sample in $_.CounterSamples) {
                    $record=[ordered]@{Utc=$stamp;Instance=$sample.InstanceName;Path=$sample.Path;Value=$sample.CookedValue;Status=$sample.Status}
                    $writer.WriteLine(($record|ConvertTo-Json -Compress));
                }
                $writer.Flush()
                if(!(Test-Path -LiteralPath $ready)){[System.IO.File]::WriteAllText($ready,'ready')}
            }
        } finally {$writer.Dispose()}
    }
    try {
        for($i=0;$i -lt 80 -and !(Test-Path -LiteralPath $ready);$i++){
            if($job.State -eq 'Failed'){Receive-Job $job;throw 'Counter monitor failed'}
            Start-Sleep -Milliseconds 250
        }
        if(!(Test-Path -LiteralPath $ready)){throw 'Counter monitor did not become ready'}
        Start-Sleep -Seconds 3
        $assembly=if($engine -eq 'Baseline'){'artifacts/baseline/PureDb.Comprehensive.dll'}else{'benchmarks/PureDb.Comprehensive/bin/Release/net10.0/PureDb.Comprehensive.dll'}
        $start=[DateTime]::UtcNow.ToString('o')
        & dotnet $assembly writes $engine $Dataset (Join-Path $physical ($engine+'-workload.json')) 1
        if($LASTEXITCODE -ne 0){throw ('Supplemental workload failed: '+$engine)}
        $end=[DateTime]::UtcNow.ToString('o')
        Start-Sleep -Seconds 2
        $records+=[ordered]@{Engine=$engine;StartUtc=$start;EndUtc=$end;CounterFile=($engine+'-counters.jsonl');Scope='Host physical disk counters, not attributable to F: or to one process';DriveMapping='Get-Partition -DriveLetter F: access denied'}
    } finally {
        Stop-Job $job
        Receive-Job $job -ErrorAction Continue | Out-Null
        Remove-Job $job
    }
}
$records|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $physical 'windows.json') -Encoding utf8
Write-Host ('Physical counters: '+$physical)
