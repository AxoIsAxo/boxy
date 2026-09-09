#!/bin/bash
declare -A seen
collect() {
  local f="$1"
  local key=$(readlink -f "$f")
  [ -n "${seen[$key]}" ] && return
  seen[$key]=1
  echo "$key"
  for d in $(ldd "$f" 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i ~ /^\//) print $i}'); do
    [ -f "$d" ] && collect "$d"
  done
}
for L in "$@"; do
  [ -f "$L" ] && collect "$L"
done
