# Fake claude-swap engine for the autodock agents tests. Synthetic only: no credentials anywhere.
# `run N --require-session -- <claude args>` plays a headless Claude Code worker; prompt markers pick
# its behaviour: [fake:quota] [fake:blocked] [fake:stall] [fake:slow] [fake:break].
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$a = $args
$log = $env:FAKE_LOG
# Concurrent calls share the logs: retry a busy file rather than fail the call over a log line.
function Log($path, $text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [IO.File]::AppendAllText($path, "$text`r`n"); return } catch { Start-Sleep -Milliseconds 25 }
    }
}
if ($log) { Log $log ($a -join ' ') }

function Emit($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 10)); [Console]::Out.Flush() }

# An Auto-Prime request (`run N … -- -p Hi … --no-session-persistence`): logs which auth variables
# reached it (presence only) and answers like `claude -p --output-format json`. FAKE_PRIME_FAIL fails it.
if ($a[0] -eq 'run' -and $a -contains '--no-session-persistence') {
    if ($log) { Log "$log.prime" ("{0}|{1}|{2}|{3}" -f [bool]$env:ANTHROPIC_API_KEY, [bool]$env:CLAUDE_CONFIG_DIR, [bool]$env:CLAUDECODE, (Get-Location).Path) }
    if ($env:FAKE_PRIME_FAIL) { [Console]::Error.WriteLine($env:FAKE_PRIME_FAIL); exit 1 }
    [Console]::Out.WriteLine("Launching Account-$($a[1]) (fake) [session mode]")
    Emit @{ type = 'result'; subtype = 'success'; is_error = $false; num_turns = 1; result = 'Hello!' }
    exit 0
}

switch ($a[0]) {
    'handoff' {
        $p = Join-Path ([IO.Path]::GetTempPath()) ('fake-handoff-' + [guid]::NewGuid().ToString('N') + '.md')
        Set-Content -LiteralPath $p -Value 'notes from the earlier session'
        Emit @{ path = $p; source = 'claude'; chars = 30 }
        exit 0
    }
    'list' {
        Emit @{ schemaVersion = 1; activeAccountNumber = 1; accounts = @(
            @{ number = 1; email = 'one@example.com'; active = $true; usageStatus = 'ok' },
            @{ number = 2; email = 'two@example.com'; active = $false; usageStatus = 'ok' }) }
        exit 0
    }
    'config' { Emit @{ schemaVersion = 1; settings = @(@{ key = 'autoswitch.threshold'; value = '90' }) }; exit 0 }
    'run' { }
    default { exit 2 }
}

$account = $a[1]
$resume = $a -contains '--resume'
$flag = if ($resume) { '--resume' } else { '--session-id' }
$session = $a[[array]::IndexOf($a, $flag) + 1]
$prompt = [Console]::In.ReadToEnd()
if ($log) {
    Log "$log.prompts" "---- account=$account resume=$resume`n$prompt"
    Log "$log.env" "$env:GH_TOKEN|$env:GIT_ALLOW_PROTOCOL|$env:CLAUDE_CONFIG_DIR"
}
[Console]::Out.WriteLine("Launching Account-$account (fake) [session mode]")
Emit @{ type = 'system'; subtype = 'init'; session_id = $session; model = 'fake-model' }

function Finish([string]$text, [bool]$isError = $false) {
    $sub = if ($isError) { 'error_during_execution' } else { 'success' }
    Emit @{ type = 'result'; subtype = $sub; is_error = $isError; result = $text; num_turns = 2; total_cost_usd = 0.01; session_id = $session }
    if ($isError) { exit 1 } else { exit 0 }
}

$n = if ($prompt -match 'issue #(\d+)') { $Matches[1] } else { 'request' }
if ($prompt -match 'You are reviewing') {
    $flag = "$log.reviewed-$n"
    if ($prompt -match '\[fake:review-changes\]' -and -not (Test-Path -LiteralPath $flag)) {
        Set-Content -LiteralPath $flag -Value 'x'
        Finish "Read the diff.`nAUTODOCK: CHANGES the greeting has no test"
    }
    Finish "Read the diff.`nAUTODOCK: APPROVED looks right"
}
if ($prompt -match '\[fake:quota\]' -and $account -eq '2') { Finish "You've hit your session limit, resets 3pm" $true }
if ($prompt -match '\[fake:blocked\]' -and $prompt -notmatch "owner's answer") { Finish "I need a decision first.`nAUTODOCK: BLOCKED Should the greeting be formal?" }
if ($prompt -match '\[fake:stall\]' -and -not $resume) { Start-Sleep -Seconds 300 }
if ($prompt -match '\[fake:long\]') {
    for ($k = 0; $k -lt 60; $k++) {
        Emit @{ type = 'assistant'; message = @{ id = "l$k"; content = @(@{ type = 'tool_use'; name = 'Read'; input = @{ file_path = "src/file-$k.cs" } }) } }
        Start-Sleep -Seconds 1
    }
}
if ($prompt -match '\[fake:slow\]') {
    for ($k = 0; $k -lt 10; $k++) {
        Emit @{ type = 'assistant'; message = @{ id = "m$k"; content = @(@{ type = 'text'; text = "step $k" }) } }
        Start-Sleep -Milliseconds 600
    }
}
Emit @{ type = 'assistant'; message = @{ id = 'w1'; content = @(@{ type = 'tool_use'; name = 'Write'; input = @{ file_path = "issue-$n.txt" } }); usage = @{ output_tokens = 42 } } }
if ($prompt -match '\[fake:break\]' -and $prompt -notmatch 'failed') { Set-Content -LiteralPath broken.txt -Value 'x' }
elseif (Test-Path broken.txt) { git rm -q broken.txt | Out-Null }
Add-Content -LiteralPath "issue-$n.txt" -Value "work by account $account"
git add -A | Out-Null
git commit -q -m "fake work for $n" | Out-Null
Finish "Did it.`nAUTODOCK: DONE added issue-$n.txt"
