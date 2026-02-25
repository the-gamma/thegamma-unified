#!/bin/sh
set -e

STORAGE="${THEGAMMA_STORAGE_ROOT:-/data/storage}"

if [ ! -f "$STORAGE/.initialized" ]; then
  echo "First startup: seeding storage volume..."
  mkdir -p "$STORAGE/uploads" "$STORAGE/snippets" "$STORAGE/datavizconfig"
  cp -rn /seed/uploads/.       "$STORAGE/uploads/"
  cp -rn /seed/snippets/.      "$STORAGE/snippets/"
  cp -rn /seed/datavizconfig/. "$STORAGE/datavizconfig/"
  touch "$STORAGE/.initialized"
  echo "Done."
fi

exec dotnet thegamma-unified.dll
