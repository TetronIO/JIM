docker kill $(docker ps -q)
docker system prune -f
docker volume rm jim-db-volume
docker volume rm jim-logs-volume

# Ensure HTTPS certificate exists for development
$certPath = "$env:APPDATA\ASP.NET\Https\aspnetapp.pfx"
if (-not (Test-Path $certPath)) {
    Write-Host "Creating HTTPS development certificate..."
    New-Item -ItemType Directory -Force -Path "$env:APPDATA\ASP.NET\Https" | Out-Null
    dotnet dev-certs https --trust
    dotnet dev-certs https -ep $certPath -p "SecurePassword123!"
    Write-Host "Certificate created at $certPath"
} else {
    Write-Host "HTTPS certificate already exists at $certPath"
}

# delete all bin and obj folders
#gci -include bin,obj -recurse | remove-item -force -recurse