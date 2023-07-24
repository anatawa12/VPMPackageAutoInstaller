#!/bin/sh

set -eu

cd "$(dirname "$0")"

rm -f VPAI-Creator-1.x.x.zip

curl -sL "https://api.anatawa12.com/create-vpai/?repo=https%3A%2F%2Fvpm.anatawa12.com%2Fvpm.json&package=com.anatawa12.vpm-package-auto-installer.creator&version=1.x" \
  > VPAI-Creator-1.x.x-installer.unitypackage

zip "VPAI-Creator-1.x.x.zip" \
  README.ja.txt \
  LICENSE.txt \
  example.png \
  VPAI-Creator-1.x.x-installer.unitypackage \
  add-repo.url

rm VPAI-Creator-1.x.x-installer.unitypackage
