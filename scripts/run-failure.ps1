$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$body = @{ sku = "VOLCAMP-63"; quantity = 1 } | ConvertTo-Json
$client = [System.Net.Http.HttpClient]::new()
$requestContent = [System.Net.Http.StringContent]::new(
    $body,
    [System.Text.Encoding]::UTF8,
    "application/json")

try {
    $response = $client.PostAsync(
        "http://localhost:5101/orders",
        $requestContent).GetAwaiter().GetResult()
    $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    if ([int]$response.StatusCode -ne 504) {
        throw "Expected HTTP 504 but received HTTP $([int]$response.StatusCode)."
    }

    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "The expected HTTP 504 response did not contain diagnostic JSON."
    }

    $detail = $content | ConvertFrom-Json
    Write-Host "Expected 504 reproduced after $($detail.attempts) attempts."
    $detail | ConvertTo-Json -Depth 10
}
finally {
    $requestContent.Dispose()
    $client.Dispose()
}
