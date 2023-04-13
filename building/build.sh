#!/bin/bash

set -eu

cd "$(dirname "$0")"
BUID_DIR="$(pwd)"
cd ".."
REPO_ROOT="$(pwd)"
INSTALLER_DIR="$REPO_ROOT/Assets/com.anatawa12.vpm-package-auto-installer"

build_dll() {
  DOTNET_DIR="$REPO_ROOT/Assets/InstallerSource/DllBuild~"
  PROFILE="Release"
  # build
  dotnet build "$DOTNET_DIR/com.anatawa12.vpm-package-auto-installer.csproj" -c "$PROFILE" 
  
  # copy dll and meta
  cp "$DOTNET_DIR/bin/$PROFILE/netstandard2.0/com.anatawa12.vpm-package-auto-installer.dll" "$INSTALLER_DIR/"
  cp "$BUID_DIR/com.anatawa12.vpm-package-auto-installer.dll.meta" "$INSTALLER_DIR/"
}

build_rs() {
  pushd "$REPO_ROOT/vrc-get" > /dev/null
  cargo +nightly build --release -Z build-std -Z build-std-features=panic-unwind "$@"
  popd > /dev/null
}

build() {
  build_dll
}

if [ $# -eq 0 ]; then
  build
else
  # you can call specific build process by parameter
  "$@"
fi
