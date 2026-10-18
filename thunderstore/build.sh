#!/bin/bash
# Builds the Thunderstore upload zips into ../thunderstore-out/
# Each zip: manifest.json, README.md, CHANGELOG.md, icon.png at the root, and the mod in GDWeave/mods/<Id>/.
set -e
cd "$(dirname "$0")"
OUT=../thunderstore-out; mkdir -p $OUT
for MOD in EtherPets WolfparkHUD HugsAndMore; do
  T=$(mktemp -d)
  cp $MOD/manifest.json $MOD/README.md $MOD/CHANGELOG.md $MOD/icon.png $T/
  mkdir -p $T/GDWeave/mods/$MOD
  cp ../$MOD/package/$MOD/manifest.json ../$MOD/package/$MOD/$MOD.pck $T/GDWeave/mods/$MOD/
  V=$(python3 -c "import json;print(json.load(open('$MOD/manifest.json'))['version_number'])")
  rm -f $OUT/$MOD-$V.zip; (cd $T && zip -qr - .) > $OUT/$MOD-$V.zip
  rm -rf $T
  echo "$OUT/$MOD-$V.zip"
done
