#!/usr/bin/env bash
# Compiles and runs the harness (dev\Stubs.cs + dev\Tests.cs + src\*.cs) with the SDK's Roslyn, outside the project.
# Usage: bash dev/run-tests.sh <output folder>
set -e
cd "$(dirname "$0")/.."
OUT="${1:?output folder}"
mkdir -p "$OUT"
CSC=$(ls -d "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll | tail -1)
REF=".build-cache/nuget-packages/microsoft.netframework.referenceassemblies.net48/1.0.3/build/.NETFramework/v4.8"
NJ=".build-cache/nuget-packages/newtonsoft.json/13.0.1/lib/net45/Newtonsoft.Json.dll"
refs=""
for r in mscorlib System System.Core System.Drawing System.ComponentModel.Composition; do refs="$refs -r:$REF/$r.dll"; done
for r in "$REF"/Facades/*.dll; do refs="$refs -r:$r"; done
cp "$NJ" "$OUT/"
dotnet "$CSC" -nologo -nowarn:CS0067,CS0436 -langversion:7.3 -nostdlib -noconfig -out:"$OUT/harness.exe" $refs -r:"$NJ" -resource:data/puzzles.json,Nax.Completion.puzzles.json -resource:data/puzzle_routes.json,Nax.Completion.puzzle_routes.json -resource:data/vista_routes.json,Nax.Completion.vista_routes.json -resource:data/map_index.json,Nax.Completion.map_index.json src/*.cs src/World/*.cs dev/Stubs.cs dev/Tests.cs
"$OUT/harness.exe"
