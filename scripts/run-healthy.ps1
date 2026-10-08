$ErrorActionPreference = "Stop"

$body = @{ sku = "DEMO-FAST"; quantity = 2 } | ConvertTo-Json
$response = Invoke-RestMethod `
    -Method Post `
    -Uri "http://localhost:5101/orders" `
    -ContentType "application/json" `
    -Body $body

$response | ConvertTo-Json -Depth 10
