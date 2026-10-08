param(
    [ValidateSet("Live", "Replay")]
    [string]$Mode = "Live",
    [string]$Question = "Pourquoi les commandes échouent-elles depuis quelques minutes ?",
    [ValidateSet("Default", "Strict")]
    [string]$GuardProfile = "Default",
    [decimal]$InputUsdPerMillion = 0.40,
    [decimal]$CachedInputUsdPerMillion = 0.10,
    [decimal]$OutputUsdPerMillion = 1.60
)

$ErrorActionPreference = "Stop"
$query = "?guardProfile=$GuardProfile"
if ($Mode -eq "Replay") { $query += "&mode=replay" }
$body = @{ question = $Question } | ConvertTo-Json

function Get-SpanAttribute {
    param(
        [Parameter(Mandatory = $true)] $Span,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $attribute = $Span.attributes | Where-Object { $_.key -eq $Name } | Select-Object -First 1
    if ($null -eq $attribute) {
        return $null
    }

    foreach ($property in @("intValue", "doubleValue", "stringValue")) {
        $value = $attribute.value.$property
        if ($null -ne $value -and "$value" -ne "") {
            return $value
        }
    }

    return $null
}

function Show-AgentUsage {
    param([Parameter(Mandatory = $true)] [string] $TraceId)

    $usageSpan = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            $trace = Invoke-RestMethod `
                -Uri "http://localhost:3200/api/traces/$TraceId" `
                -TimeoutSec 5

            $spans = @(
                foreach ($batch in $trace.batches) {
                    foreach ($scope in $batch.scopeSpans) {
                        foreach ($span in $scope.spans) {
                            $span
                        }
                    }
                }
            )

            $usageSpan = $spans |
                Where-Object {
                    $_.name -like "invoke_agent *" -and
                    $null -ne (Get-SpanAttribute -Span $_ -Name "gen_ai.usage.input_tokens")
                } |
                Select-Object -First 1

            if ($null -eq $usageSpan) {
                $usageSpan = $spans |
                    Where-Object {
                        $_.name -like "chat *" -and
                        $null -ne (Get-SpanAttribute -Span $_ -Name "gen_ai.usage.input_tokens")
                    } |
                    Select-Object -First 1
            }

            if ($null -ne $usageSpan) {
                break
            }
        }
        catch {
        }

        if ($attempt -lt 6) {
            Start-Sleep -Seconds 2
        }
    }

    if ($null -eq $usageSpan) {
        Write-Warning "La consommation GenAI n'est pas encore indexee dans Tempo. Consultez la trace dans quelques secondes."
        return
    }

    $inputTokens = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.usage.input_tokens"
    $outputTokens = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.usage.output_tokens"
    $cacheReadTokens = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.usage.cache_read.input_tokens"
    $reasoningTokens = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.usage.reasoning.output_tokens"
    $model = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.response.model"
    if ([string]::IsNullOrWhiteSpace("$model")) {
        $model = Get-SpanAttribute -Span $usageSpan -Name "gen_ai.request.model"
    }

    Write-Host "=== Consommation GenAI ===" -ForegroundColor Cyan
    Write-Host "Modele              : $model"
    Write-Host "Tokens en entree    : $inputTokens"
    Write-Host "Tokens en sortie    : $outputTokens"
    if ($null -ne $cacheReadTokens) {
        Write-Host "Tokens lus du cache : $cacheReadTokens"
    }
    if ($null -ne $reasoningTokens) {
        Write-Host "Tokens de raisonnement : $reasoningTokens"
    }

    if ($Mode -eq "Replay") {
        Write-Host "Cout reel           : 0 USD (aucun appel Azure OpenAI)"
        Write-Host ""
        return
    }

    $input = [decimal]$inputTokens
    $output = [decimal]$outputTokens
    $cachedInput = if ($null -eq $cacheReadTokens) { 0 } else { [decimal]$cacheReadTokens }
    $uncachedInput = [Math]::Max([decimal]0, $input - $cachedInput)
    $estimatedCostUsd =
        ($uncachedInput * $InputUsdPerMillion / 1000000) +
        ($cachedInput * $CachedInputUsdPerMillion / 1000000) +
        ($output * $OutputUsdPerMillion / 1000000)

    Write-Host ("Cout estime         : {0:N6} USD" -f $estimatedCostUsd)
    Write-Host (
        "Tarifs utilises     : entree {0} / cache {1} / sortie {2} USD par million" -f
        $InputUsdPerMillion,
        $CachedInputUsdPerMillion,
        $OutputUsdPerMillion)
    Write-Host ""
}

function Show-Guard {
    param([Parameter(Mandatory = $true)] $Response)

    $guard = $Response.guard
    if ($null -eq $guard) {
        return
    }

    if ($Response.mode -eq "replay") {
        Write-Host "=== Guard : non applique en replay (aucun appel au modele) ===" -ForegroundColor Yellow
        Write-Host ""
        return
    }

    if ($Response.guardTriggered) {
        Write-Host ("=== GUARD DECLENCHE : {0} ({1} / {2} {3}) ===" -f
            $guard.reason, $guard.consumed, $guard.limit, $guard.unit) -ForegroundColor Red
    }
    else {
        Write-Host "=== Guard : aucune limite atteinte ===" -ForegroundColor Green
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    Write-Host "Profil              : $($guard.profile)"
    Write-Host ("Duree               : {0} s / {1} s" -f $guard.durationSeconds, $guard.maxDurationSeconds)
    Write-Host ("Tokens              : {0} / {1}" -f $guard.totalTokens, $guard.maxTotalTokens)
    Write-Host ([string]::Format($culture, "Cout estime (guard) : {0:0.000000} / {1:0.00} USD", [double]$guard.estimatedCostUsd, [double]$guard.maxCostUsd))
    Write-Host ("Appels d'outils     : {0} / {1}" -f $guard.toolCalls, $guard.maxToolCalls)
    if ($guard.globalBudgetChecked) {
        Write-Host ([string]::Format($culture, "Budget global 1h    : {0:0.000000} / {1:0.00} USD", [double]$guard.globalCostLastHourUsd, [double]$guard.globalBudgetUsdPerHour))
    }
    else {
        Write-Host "Budget global 1h    : non verifie (Prometheus indisponible ou replay)"
    }
    Write-Host ""
}

try {
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri "http://localhost:5104/diagnose$query" `
        -ContentType "application/json" `
        -Body $body
    Write-Host ""
    Write-Host "=== Diagnostic de l'agent ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host $response.diagnosis
    Write-Host ""
    Write-Host "Mode       : $($response.mode)"
    Write-Host "Trace agent: $($response.traceId)"
    Write-Host "Tentatives : $($response.llmAttempts)"
    Write-Host ""
    Show-Guard -Response $response
    Show-AgentUsage -TraceId $response.traceId
}
catch {
    if ($Mode -eq "Live") {
        $detail = $_.Exception.Message
        if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            try {
                $problem = $_.ErrorDetails.Message | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace($problem.detail)) {
                    $detail = $problem.detail
                }
            }
            catch {
                $detail = $_.ErrorDetails.Message
            }
        }

        Write-Error "Le diagnostic en direct a échoué : $detail Lancez '.\scripts\run-agent.ps1 -Mode Replay' pour utiliser le mode de secours."
    }
    throw
}
