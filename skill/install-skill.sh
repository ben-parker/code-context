#!/usr/bin/env sh
# Installs the canonical CodeContext skill for Codex and Claude Code.
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
skill_source="$script_dir/SKILL.md"
references_source="$script_dir/references"
[ -f "$skill_source" ] || { echo "Not found: $skill_source" >&2; exit 1; }
codex_home=${CODEX_HOME:-"$HOME/.codex"}
for target_dir in "$codex_home/skills/code-context" "$HOME/.claude/skills/code-context"; do
    mkdir -p "$target_dir"
    cp "$skill_source" "$target_dir/SKILL.md"
    if [ -d "$references_source" ]; then
        rm -rf "$target_dir/references"
        cp -R "$references_source" "$target_dir/references"
    fi
    echo "Installed CodeContext skill to $target_dir"
done

echo "Start a new agent session to pick up the skill."
