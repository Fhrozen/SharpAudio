#!/usr/bin/env bash
# Stamps one-line wiki headers on mapped source files, derived from the
# `sections:` block of each docs/wiki/*.md frontmatter. Idempotent.
#
#   ./tools/wiki/apply-headers.sh [--dry-run]
#
# Rule: a header is written only for folders containing more than one mapped
# source file; single-file folders are mapped by the wiki `sections:` entry
# alone. Files named Program.cs are the exception and always get one.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
wiki_dir="$root/docs/wiki"
dry_run=0
[ "${1:-}" = "--dry-run" ] && dry_run=1

manifest="$(mktemp)"
trap 'rm -f "$manifest"' EXIT

# wiki_slug <TAB> anchor <TAB> repo-relative-path
for f in "$wiki_dir"/*.md; do
  awk -v slug="" '
    BEGIN { in_fm = 0; in_sections = 0 }
    NR == 1 && $0 == "---" { in_fm = 1; next }
    in_fm && $0 == "---" { exit }
    in_fm && $0 ~ /^wiki: / { slug = substr($0, 7); next }
    in_fm && $0 == "sections:" { in_sections = 1; next }
    in_fm && in_sections && /^  [a-z0-9-]+:$/ {
      anchor = substr($0, 3, length($0) - 3); next
    }
    in_fm && in_sections && /^    - / {
      print slug "\t" anchor "\t" substr($0, 7); next
    }
  ' "$f" >> "$manifest"
done

if [ ! -s "$manifest" ]; then
  echo "no sections: entries found in $wiki_dir" >&2
  exit 1
fi

# folder <TAB> mapped-file-count
counts="$(awk -F'\t' '{ n = split($3, p, "/"); d = ".";
  for (i = 1; i < n; i++) { d = (i == 1 ? p[i] : d "/" p[i]) };
  print d }' "$manifest" | sort | uniq -c | awk '{ print $2 "\t" $1 }')"

comment_for() {
  local name
  name="$(basename "$1")"
  case "$name" in
    Dockerfile|Dockerfile.*) printf '# wiki: %s' "$2" ;;
    *.sh|*.ps1)              printf '# wiki: %s' "$2" ;;
    *.vue|*.csproj)          printf '<!-- wiki: %s -->' "$2" ;;
    *)                       printf '// wiki: %s' "$2" ;;
  esac
}

changed=0
while IFS=$'\t' read -r slug anchor path; do
  file="$root/$path"
  [ -f "$file" ] || continue

  folder="$(dirname "$path")"
  count="$(awk -F'\t' -v d="$folder" '$1 == d { print $2; exit }' <<< "$counts")"
  [ -z "$count" ] && count=1
  if [ "$count" -le 1 ] && [ "$(basename "$path")" != "Program.cs" ]; then
    continue
  fi

  header="$(comment_for "$path" "$slug#$anchor")"

  # Locate the header slot: line 1, or line 2 when line 1 is a shebang.
  first="$(head -1 "$file")"
  slot=1
  case "$first" in
    '#!'*) slot=2 ;;
  esac

  existing="$(sed -n "${slot}p" "$file")"
  case "$existing" in
    *wiki:*)
      [ "$existing" = "$header" ] && continue
      if [ "$dry_run" = 1 ]; then
        echo "would update: $path -> $header"
      else
        sed -i "${slot}s|.*|$header|" "$file"
        echo "updated: $path -> $header"
      fi
      changed=$((changed + 1))
      ;;
    '')
      if [ "$dry_run" = 1 ]; then echo "would create: $path -> $header"; else
        printf '%s\n' "$header" > "$file"; echo "created: $path -> $header"; fi
      changed=$((changed + 1))
      ;;
    *)
      if [ "$dry_run" = 1 ]; then
        echo "would insert at line $slot: $path -> $header"
      else
        tmp="$(mktemp)"
        head -n $((slot - 1)) "$file" > "$tmp"
        printf '%s\n' "$header" >> "$tmp"
        tail -n +"$slot" "$file" >> "$tmp"
        chmod "$(stat -c '%a' "$file")" "$tmp"
        mv "$tmp" "$file"
        echo "inserted at line $slot: $path -> $header"
      fi
      changed=$((changed + 1))
      ;;
  esac
done < "$manifest"

echo "$changed header(s) changed."
