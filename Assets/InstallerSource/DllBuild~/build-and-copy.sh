#!/bin/bash

cd "$(dirname "$0")"

# build
dotnet build VPMPackageAutoInstaller.csproj -c Release 

# copy dll and meta
cp ./bin/Release/netstandard2.0/VPMPackageAutoInstaller.dll ../../com.anatawa12.vpm-package-auto-installer/
cp ./VPMPackageAutoInstaller.dll.meta ../../com.anatawa12.vpm-package-auto-installer/
