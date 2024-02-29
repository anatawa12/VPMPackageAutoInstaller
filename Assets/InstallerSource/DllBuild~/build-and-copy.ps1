$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

dotnet build com.anatawa12.vpm-package-auto-installer.csproj -c Release

Copy-Item -Path ".\bin\Release\netstandard2.0\com.anatawa12.vpm-package-auto-installer.dll" -Destination "..\..\com.anatawa12.vpm-package-auto-installer\" -Force
Copy-Item -Path ".\com.anatawa12.vpm-package-auto-installer.dll.meta" -Destination "..\..\com.anatawa12.vpm-package-auto-installer\" -Force
