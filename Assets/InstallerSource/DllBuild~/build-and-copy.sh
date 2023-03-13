#!/bin/bash

set -eu

cd "$(dirname "$0")"

# build
dotnet build com.anatawa12.vpm-package-auto-installer.csproj -c Release 

# copy dll and meta
cp ./bin/Release/netstandard2.0/com.anatawa12.vpm-package-auto-installer.dll ../../com.anatawa12.vpm-package-auto-installer/
cp ./com.anatawa12.vpm-package-auto-installer.dll.meta ../../com.anatawa12.vpm-package-auto-installer/
