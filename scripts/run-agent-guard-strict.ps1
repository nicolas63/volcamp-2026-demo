param(
    [string]$Question
)

# Strict profile: 8 000 tokens and 2 tool calls, so the guard triggers on stage.
$arguments = @{ Mode = "Live"; GuardProfile = "Strict" }
if ($PSBoundParameters.ContainsKey("Question")) {
    $arguments.Question = $Question
}

& "$PSScriptRoot\run-agent.ps1" @arguments
