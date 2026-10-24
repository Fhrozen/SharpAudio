#!/usr/bin/env bash
# Checks the wiki-header convention described in docs/LLM_WIKI.md:
#   1. every mapped source file is owned by exactly one wiki file, inside its covers
#   2. every `sections:` anchor is an actual heading in that wiki file
#   3. every wiki file has an `updated` date, and it is not stale relative to git
#   4. headers match the `sections:` mapping for folders with more than one mapped file
#
#   ./tools/wiki/wiki-check.sh
#
# The same rules 1-4 run in CI as WikiHeaderCoverageTests; staleness (rule 3) needs git
# history, so it is checked here only.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
wiki_dir="$root/docs/wiki"

problems=0
fail() {
  echo "FAIL: $1" >&2
  problems=$((problems + 1))
}

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

is_mapped() {
  case "$1" in
    */node_modules/*|*/bin/*|*/obj/*|*/.git/*|*/out_refs/*|*/TestData/*|tools/*|.*/*) return 1 ;;
  esac
  case "$(basename "$1")" in
    Dockerfile|Dockerfile.*) return 0 ;;
  esac
  case "$1" in
    *.cs|*.proto|*.vue|*.ts|*.js|*.sh|*.ps1) return 0 ;;
  esac
  return 1
}

manifest="$(mktemp)"
tmp_anchors="$(mktemp)"
trap 'rm -f "$manifest" "$tmp_anchors"' EXIT

# wiki_slug <TAB> anchor <TAB> repo-relative-path <TAB> wiki-file
for f in "$wiki_dir"/*.md; do
  awk -v wf="$(basename "$f")" '
    BEGIN { in_fm = 0; in_sections = 0 }
    NR == 1 && $0 == "---" { in_fm = 1; next }
    in_fm && $0 == "---" { exit }
    in_fm && $0 ~ /^wiki: / { slug = substr($0, 7); next }
    in_fm && $0 == "covers:" { in_covers = 1; in_sections = 0; next }
    in_fm && in_covers && /^  - / { print slug "\tCOVERS\t" substr($0, 5) "\t" wf; next }
    in_fm && $0 == "sections:" { in_covers = 0; in_sections = 1; next }
    in_fm && in_sections && /^  [a-z0-9-]+:$/ {
      anchor = substr($0, 3, length($0) - 3); next
    }
    in_fm && in_sections && /^    - / {
      print slug "\t" anchor "\t" substr($0, 7) "\t" wf; next
    }
  ' "$f" >> "$manifest"
done

# Rule 1: ownership + covers
owned="$(awk -F'\t' '$2 != "COVERS" { print $3 }' "$manifest" | sort)"
duplicate="$(sort <<< "$owned" | uniq -d)"
if [ -n "$duplicate" ]; then
  while IFS= read -r path; do fail "$path is owned by more than one wiki file"; done <<< "$duplicate"
fi

found=0
while IFS= read -r file; do
  found=$((found + 1))
  slug="$(awk -F'\t' -v p="$file" '$2 != "COVERS" && $3 == p { print $1; exit }' "$manifest")"
  if [ -z "$slug" ]; then
    fail "$file is not mapped by any wiki file"
    continue
  fi
  inside=0
  while IFS=$'\t' read -r s anchor glob wf; do
    [ "$anchor" = "COVERS" ] && [ "$s" = "$slug" ] || continue
    pattern="^$(echo "$glob" | sed 's|\*\*|@@|g; s|\*|[^/]*|g; s|@@|.*|g')$"
    if [[ $file =~ $pattern ]]; then inside=1; break; fi
  done < <(awk -F'\t' '$2 == "COVERS"' "$manifest")
  [ $inside -eq 1 ] || fail "$file is owned by $slug but outside its covers"
done < <(
  cd "$root"
  find . -type f \( -name '*.cs' -o -name '*.proto' -o -name '*.vue' -o -name '*.ts' \
    -o -name '*.js' -o -name '*.sh' -o -name '*.ps1' -o -name 'Dockerfile*' \) \
    | sed 's|^\./||' \
    | grep -v -E '(^|/)(bin|obj|node_modules|out_refs|TestData|tools|Properties|\.docker-cache)/' \
    | sort
)
echo "checked $found mapped file(s)"

# Rule 2: anchors must be real headings
for f in "$wiki_dir"/*.md; do
  awk -v wf="$(basename "$f")" '
    BEGIN { in_fm = 0 }
    NR == 1 && $0 == "---" { in_fm = 1; next }
    in_fm && $0 == "---" { in_fm = 0; next }
    in_fm && /^  [a-z0-9-]+:$/ { anchors[++n] = substr($0, 3, length($0) - 3); next }
    !in_fm && /^#+ / {
      h = tolower(substr($0, 3))
      gsub(/[^a-z0-9]+/, "-", h)
      sub(/^-/, "", h); sub(/-$/, "", h)
      seen[h] = 1
    }
    END {
      for (i = 1; i <= n; i++) if (!(anchors[i] in seen)) {
        printf "%s: anchor %s is not a heading in %s\n", wf, anchors[i], wf
      }
    }
  ' "$f" > "$tmp_anchors"
  while IFS= read -r line; do fail "$line"; done < "$tmp_anchors"
done

# Rule 3: updated dates and git staleness
counts="$(awk -F'\t' '{ n = split($3, p, "/"); d = "."
  for (i = 1; i < n; i++) d = (i == 1 ? p[i] : d "/" p[i]); print d }' "$manifest" \
  | sort | uniq -c | awk '{ print $2 "\t" $1 }')"

for f in "$wiki_dir"/*.md; do
  updated="$(awk 'NR == 1 && $0 == "---" { fm = 1; next } fm && /^updated: / { print substr($0, 10); exit }' "$f")"
  [ -n "$updated" ] || { fail "$(basename "$f"): no updated date"; continue; }

  paths="$(awk -F'\t' -v wf="$(basename "$f")" '$4 == wf && $2 != "COVERS" { print $3 }' "$manifest")"
  [ -n "$paths" ] || continue

  latest="$(git -C "$root" log -1 --format=%ad --date=short -- $paths 2>/dev/null || true)"
  if [ -n "$latest" ] && [[ $latest > $updated ]]; then
    fail "$(basename "$f"): updated $updated is stale, mapped files changed on $latest"
  fi

  # Rule 4: headers
  while IFS=$'\t' read -r slug anchor path wf; do
    [ "$anchor" = "COVERS" ] && continue
    [ "$wf" != "$(basename "$f")" ] && continue
    folder="$(dirname "$path")"
    count="$(awk -F'\t' -v d="$folder" '$1 == d { print $2; exit }' <<< "$counts")"
    [ -z "$count" ] && count=1
    [ "$count" -le 1 ] && [ "$(basename "$path")" != "Program.cs" ] && continue
    slot=1
    case "$(head -1 "$root/$path")" in '#!'*) slot=2 ;; esac
    expected="$(comment_for "$path" "$slug#$anchor")"
    actual="$(sed -n "${slot}p" "$root/$path")"
    [ "$actual" = "$expected" ] || fail "$path: header '$actual' should be '$expected'"
  done <<< "$manifest"
done

if [ $problems -gt 0 ]; then
  echo ""
  echo "$problems problem(s) found." >&2
  exit 1
fi

echo "wiki check passed."
