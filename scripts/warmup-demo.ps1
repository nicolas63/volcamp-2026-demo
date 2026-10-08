param(
    [int]$Count = 5
)

$ErrorActionPreference = "Stop"

for ($index = 1; $index -le $Count; $index++) {
    $body = @{ sku = "DEMO-FAST"; quantity = 1 } | ConvertTo-Json
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri "http://localhost:5101/orders" `
        -ContentType "application/json" `
        -Body $body
    Write-Host "Warm-up order $index/$Count accepted: $($response.orderId)"
}
