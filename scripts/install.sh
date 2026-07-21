#!/usr/bin/env sh
# Installs the latest CodeContext release to ~/.codecontext/bin (Linux/macOS).
# Usage: curl -fsSL https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.sh | sh
set -eu

repo='ben-parker/code-context'
codecontext_home="$HOME/.codecontext"
install_dir="$codecontext_home/bin"
tmp_dir=''
tty_state=''

cleanup() {
  if [ -n "$tty_state" ]; then
    stty "$tty_state" <&3 2>/dev/null || true
    tty_state=''
  fi
  if [ -n "$tmp_dir" ] && [ -d "$tmp_dir" ]; then
    rm -rf "$tmp_dir"
  fi
}

cancel_install() {
  cleanup
  printf '\nInstallation cancelled.\n' >&2
  trap - EXIT HUP INT TERM
  exit 130
}

trap cleanup EXIT
trap cancel_install HUP INT TERM

draw_skill_menu() {
  if [ "$menu_drawn" = true ]; then
    printf '\033[6A' >&3
  fi
  menu_drawn=true
  row=0
  while [ "$row" -lt 6 ]; do
    case "$row" in
      0) checked=$skill_shared; label='Shared agents'; hint='~/.agents/skills' ;;
      1) checked=$skill_claude; label='Claude Code'; hint='~/.claude/skills' ;;
      2) checked=$skill_devin; label='Devin Desktop'; hint='~/.codeium/windsurf/skills' ;;
      3) checked=$skill_codex; label='Codex (legacy)'; hint='~/.codex/skills' ;;
      4) checked=$skill_cursor; label='Cursor'; hint='~/.cursor/skills' ;;
      5) checked=$skill_gemini; label='Gemini CLI'; hint='~/.gemini/skills' ;;
    esac
    if [ "$row" -eq "$menu_cursor" ]; then pointer='>'; else pointer=' '; fi
    if [ "$checked" = true ]; then mark='x'; else mark=' '; fi
    printf '\r\033[2K%s [%s] %-15s (%s)\n' "$pointer" "$mark" "$label" "$hint" >&3
    row=$((row + 1))
  done
}

select_skill_targets() {
  # Pre-select targets that already have a code-context skill installed, so an upgrade
  # refreshes exactly what's there instead of re-asking about every possible agent. Only
  # when nothing is detected anywhere do we fall back to the original default (shared only).
  skill_shared=false
  skill_claude=false
  skill_devin=false
  skill_codex=false
  skill_cursor=false
  skill_gemini=false
  any_existing_skill=false
  target_exists "$HOME/.agents/skills/code-context" && { skill_shared=true; any_existing_skill=true; }
  target_exists "$HOME/.claude/skills/code-context" && { skill_claude=true; any_existing_skill=true; }
  target_exists "$HOME/.codeium/windsurf/skills/code-context" && { skill_devin=true; any_existing_skill=true; }
  target_exists "$codex_home/skills/code-context" && { skill_codex=true; any_existing_skill=true; }
  target_exists "$HOME/.cursor/skills/code-context" && { skill_cursor=true; any_existing_skill=true; }
  target_exists "$HOME/.gemini/skills/code-context" && { skill_gemini=true; any_existing_skill=true; }
  if [ "$any_existing_skill" = false ]; then
    skill_shared=true
  fi
  menu_cursor=0
  menu_drawn=false

  if [ ! -e /dev/tty ] || ! ( : <>/dev/tty ) 2>/dev/null; then
    echo "No interactive terminal is available; skipping agent skill installation."
    skills_interactive=false
    return
  fi
  exec 3<>/dev/tty
  tty_state=$(stty -g <&3 2>/dev/null) || {
    echo "No usable interactive terminal is available; skipping agent skill installation."
    skills_interactive=false
    exec 3>&-
    return
  }

  skills_interactive=true
  printf '\nSelect agent skill targets (space to toggle, enter to continue):\n\n' >&3
  if ! stty -echo -icanon min 1 time 0 <&3 2>/dev/null; then
    echo "No usable interactive terminal is available; skipping agent skill installation."
    skills_interactive=false
    tty_state=''
    exec 3>&-
    return
  fi
  draw_skill_menu
  while :; do
    key=$(dd bs=1 count=1 <&3 2>/dev/null) || key=''
    case "$key" in
      '') break ;;
      ' ')
        case "$menu_cursor" in
          0) if [ "$skill_shared" = true ]; then skill_shared=false; else skill_shared=true; fi ;;
          1) if [ "$skill_claude" = true ]; then skill_claude=false; else skill_claude=true; fi ;;
          2) if [ "$skill_devin" = true ]; then skill_devin=false; else skill_devin=true; fi ;;
          3) if [ "$skill_codex" = true ]; then skill_codex=false; else skill_codex=true; fi ;;
          4) if [ "$skill_cursor" = true ]; then skill_cursor=false; else skill_cursor=true; fi ;;
          5) if [ "$skill_gemini" = true ]; then skill_gemini=false; else skill_gemini=true; fi ;;
        esac
        draw_skill_menu
        ;;
      "$(printf '\033')")
        key2=$(dd bs=1 count=1 <&3 2>/dev/null) || key2=''
        key3=$(dd bs=1 count=1 <&3 2>/dev/null) || key3=''
        if [ "$key2$key3" = '[A' ]; then
          if [ "$menu_cursor" -eq 0 ]; then menu_cursor=5; else menu_cursor=$((menu_cursor - 1)); fi
          draw_skill_menu
        elif [ "$key2$key3" = '[B' ]; then
          if [ "$menu_cursor" -eq 5 ]; then menu_cursor=0; else menu_cursor=$((menu_cursor + 1)); fi
          draw_skill_menu
        fi
        ;;
    esac
  done
  stty "$tty_state" <&3
  tty_state=''
}

target_exists() {
  [ -e "$1" ] || [ -L "$1" ]
}

confirm_skill_overwrite() {
  target_label=$1
  target_path=$2
  printf 'Overwrite %s at %s? [y/N] ' "$target_label" "$target_path" >&3
  answer=''
  IFS= read -r answer <&3 || true
  case "$answer" in
    y|Y|yes|YES|Yes) return 0 ;;
    *) return 1 ;;
  esac
}

plan_skill_target() {
  plan_key=$1
  plan_label=$2
  plan_path=$3
  plan_selected=$4
  plan_action=none
  if [ "$plan_selected" = true ]; then
    selected_skill_count=$((selected_skill_count + 1))
    if target_exists "$plan_path"; then
      if confirm_skill_overwrite "$plan_label" "$plan_path"; then
        plan_action=replace
      else
        plan_action=skip
      fi
    else
      plan_action=new
    fi
  fi
  eval "skill_action_$plan_key=\$plan_action"
}

append_summary() {
  summary_kind=$1
  summary_text=$2
  eval "summary_current=\${skill_${summary_kind}_summary:-}"
  if [ -n "$summary_current" ]; then summary_current="$summary_current, $summary_text"; else summary_current=$summary_text; fi
  eval "skill_${summary_kind}_summary=\$summary_current"
}

install_skill_target() {
  target_label=$1
  target_path=$2
  target_action=$3
  skill_source=$4
  parent_dir=$(dirname "$target_path")
  staged_dir=''
  backup_dir="$parent_dir/.code-context.backup.$$"

  if [ "$target_action" = skip ]; then
    append_summary skipped "$target_label"
    return 0
  fi
  [ "$target_action" != none ] || return 0
  if [ "$target_action" = new ] && target_exists "$target_path"; then
    echo "Warning: $target_label skill target appeared after confirmation; preserving $target_path." >&2
    append_summary failed "$target_label"
    return 0
  fi
  if ! mkdir -p "$parent_dir"; then
    echo "Warning: could not create parent directory for $target_label skill at $target_path." >&2
    append_summary failed "$target_label"
    return 0
  fi
  if target_exists "$backup_dir"; then
    echo "Warning: temporary backup path already exists for $target_label skill: $backup_dir." >&2
    append_summary failed "$target_label"
    return 0
  fi
  staged_dir=$(mktemp -d "$parent_dir/.code-context.new.XXXXXX" 2>/dev/null) || {
    echo "Warning: could not stage $target_label skill beside $target_path." >&2
    append_summary failed "$target_label"
    return 0
  }
  if ! cp "$skill_source/SKILL.md" "$staged_dir/SKILL.md" ||
     ! cp -R "$skill_source/references" "$staged_dir/references"; then
    rm -rf "$staged_dir"
    echo "Warning: could not stage the complete $target_label skill." >&2
    append_summary failed "$target_label"
    return 0
  fi

  moved_original=false
  if target_exists "$target_path"; then
    if ! mv "$target_path" "$backup_dir"; then
      rm -rf "$staged_dir"
      echo "Warning: could not move the existing $target_label skill aside; it was left unchanged." >&2
      append_summary failed "$target_label"
      return 0
    fi
    moved_original=true
  fi
  if ! mv "$staged_dir" "$target_path"; then
    if [ "$moved_original" = true ]; then
      mv "$backup_dir" "$target_path" 2>/dev/null ||
        echo "Warning: automatic restore failed for $target_label; the original remains at $backup_dir." >&2
    fi
    rm -rf "$staged_dir"
    echo "Warning: could not install the $target_label skill at $target_path." >&2
    append_summary failed "$target_label"
    return 0
  fi
  if [ "$moved_original" = true ]; then
    rm -rf "$backup_dir" || echo "Warning: could not remove temporary backup $backup_dir." >&2
  fi
  append_summary installed "$target_label"
}

print_skill_summary() {
  echo "Agent skill installation summary:"
  echo "  Installed: ${skill_installed_summary:-none}"
  echo "  Skipped: ${skill_skipped_summary:-none}"
  echo "  Failed: ${skill_failed_summary:-none}"
}

# Run installer tools from a stable directory instead of inheriting an arbitrary
# caller directory. This avoids macOS child-process getcwd failures during install.
cd "$HOME"

os=$(uname -s)
arch=$(uname -m)
case "$os" in
  Linux)  rid='linux-x64' ;;
  Darwin) if [ "$arch" = 'arm64' ]; then rid='osx-arm64'; else rid='osx-x64'; fi ;;
  *) echo "Unsupported OS: $os" >&2; exit 1 ;;
esac

# Swapping the launcher/release payload out from under a running instance can corrupt
# an in-flight index or crash it outright, so refuse to install while any are up.
existing_launcher="$install_dir/codecontext"
if [ -x "$existing_launcher" ]; then
  running_output=$("$existing_launcher" list 2>/dev/null) || running_output=""
  if [ -n "$running_output" ] && [ "$running_output" != "No running instances." ]; then
    echo "CodeContext is currently running:" >&2
    echo "$running_output" >&2
    echo "" >&2
    echo "Stop all running instances first, then rerun the installer:" >&2
    echo "  codecontext stop --all" >&2
    exit 1
  fi
fi

echo "Resolving latest release for $rid..."
url=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
  | grep -o "\"browser_download_url\": *\"[^\"]*-$rid\.zip\"" \
  | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
[ -n "$url" ] || { echo "No asset found for $rid." >&2; exit 1; }

tmp_dir=$(mktemp -d /tmp/codecontext.XXXXXX)
tmp_zip="$tmp_dir/release.zip"
echo "Downloading $url..."
if ! curl -fL --progress-bar \
    --retry 3 --retry-delay 1 \
    --connect-timeout 15 --speed-limit 1024 --speed-time 30 \
    -o "$tmp_zip" "$url"; then
  echo "Download failed. Check access to github.com and release-assets.githubusercontent.com, then retry." >&2
  exit 1
fi

echo "Download complete. Extracting release..."
stage_dir="$tmp_dir/payload"
mkdir -p "$stage_dir"
unzip -oq "$tmp_zip" -d "$stage_dir"
[ -f "$stage_dir/codecontext" ] || { echo "Release is missing codecontext." >&2; exit 1; }
[ -f "$stage_dir/workers/csharp/worker-manifest.json" ] || { echo "Release is missing C# worker assets." >&2; exit 1; }
[ -f "$stage_dir/workers/typescript/worker-manifest.json" ] || { echo "Release is missing TypeScript worker assets." >&2; exit 1; }
[ -f "$stage_dir/skill/SKILL.md" ] || { echo "Release is missing skill/SKILL.md." >&2; exit 1; }
[ -f "$stage_dir/skill/references/native-syntax.md" ] || { echo "Release is missing skill/references/native-syntax.md." >&2; exit 1; }
[ -f "$stage_dir/skill/references/operations.md" ] || { echo "Release is missing skill/references/operations.md." >&2; exit 1; }

# Gather every interactive choice before changing the installed release or launcher.
codex_home=${CODEX_HOME:-"$HOME/.codex"}
select_skill_targets
selected_skill_count=0
skill_action_shared=none
skill_action_claude=none
skill_action_devin=none
skill_action_codex=none
skill_action_cursor=none
skill_action_gemini=none
if [ "$skills_interactive" = true ]; then
  plan_skill_target shared 'Shared agents' "$HOME/.agents/skills/code-context" "$skill_shared"
  plan_skill_target claude 'Claude Code' "$HOME/.claude/skills/code-context" "$skill_claude"
  plan_skill_target devin 'Devin Desktop' "$HOME/.codeium/windsurf/skills/code-context" "$skill_devin"
  plan_skill_target codex 'Codex (legacy)' "$codex_home/skills/code-context" "$skill_codex"
  plan_skill_target cursor 'Cursor' "$HOME/.cursor/skills/code-context" "$skill_cursor"
  plan_skill_target gemini 'Gemini CLI' "$HOME/.gemini/skills/code-context" "$skill_gemini"
  exec 3>&-
fi

asset_name=$(basename "$url")
tag=${asset_name#codecontext-}
tag=${tag%-$rid.zip}
[ -n "$tag" ] || tag="release-$(date +%Y%m%d%H%M%S)"
release_dir="$codecontext_home/releases/$tag"
mkdir -p "$codecontext_home/releases" "$install_dir"
if [ ! -d "$release_dir" ]; then
  mv "$stage_dir" "$release_dir"
fi
chmod +x "$release_dir/codecontext" "$release_dir/workers/csharp/CodeContext.CSharp.Worker" "$release_dir/workers/typescript/node"

# The stable launcher is replaced atomically. Running instances keep using their
# versioned payload, so an upgrade cannot mix host and worker versions.
launcher_tmp="$install_dir/.codecontext.new"
cat > "$launcher_tmp" <<EOF
#!/usr/bin/env sh
exec "$release_dir/codecontext" "\$@"
EOF
chmod +x "$launcher_tmp"
mv -f "$launcher_tmp" "$install_dir/codecontext"

# macOS Gatekeeper quarantines unsigned downloads.
if [ "$os" = 'Darwin' ]; then
  xattr -dr com.apple.quarantine "$release_dir" 2>/dev/null || true
fi

# Skill failures are isolated from the binary installation and from other targets.
if [ "$skills_interactive" = true ]; then
  skill_installed_summary=''
  skill_skipped_summary=''
  skill_failed_summary=''
  install_skill_target 'Shared agents' "$HOME/.agents/skills/code-context" "$skill_action_shared" "$release_dir/skill"
  install_skill_target 'Claude Code' "$HOME/.claude/skills/code-context" "$skill_action_claude" "$release_dir/skill"
  install_skill_target 'Devin Desktop' "$HOME/.codeium/windsurf/skills/code-context" "$skill_action_devin" "$release_dir/skill"
  install_skill_target 'Codex (legacy)' "$codex_home/skills/code-context" "$skill_action_codex" "$release_dir/skill"
  install_skill_target 'Cursor' "$HOME/.cursor/skills/code-context" "$skill_action_cursor" "$release_dir/skill"
  install_skill_target 'Gemini CLI' "$HOME/.gemini/skills/code-context" "$skill_action_gemini" "$release_dir/skill"
  if [ "$selected_skill_count" -eq 0 ]; then
    skill_skipped_summary='all targets (none selected)'
  fi
  print_skill_summary
fi

# The launcher now points at the new release, so old ones are dead weight.
removal_noted=false
for old_dir in "$codecontext_home"/releases/*/; do
  old_dir=${old_dir%/}
  [ -d "$old_dir" ] || continue
  [ "$old_dir" = "$release_dir" ] && continue
  if [ "$removal_noted" = false ]; then
    echo "Removing previous CodeContext versions..."
    removal_noted=true
  fi
  rm -rf "$old_dir"
done

echo "Installed $tag to $release_dir"
case ":$PATH:" in
  *":$install_dir:"*) echo "Run: codecontext --version" ;;
  *)
    shell_path=${SHELL:-}
    shell_name=${shell_path##*/}
    case "$shell_name" in
      zsh)  profile="$HOME/.zshrc" ;;
      bash) profile="$HOME/.bashrc" ;;
      fish) profile="$HOME/.config/fish/config.fish" ;;
      *)    profile="$HOME/.profile" ;;
    esac
    mkdir -p "$(dirname "$profile")"
    if [ ! -f "$profile" ] || ! grep -F "$install_dir" "$profile" >/dev/null 2>&1; then
      if [ "$shell_name" = fish ]; then
        printf '\n# CodeContext\nfish_add_path "%s"\n' "$install_dir" >> "$profile"
      else
        printf '\n# CodeContext\nexport PATH="$PATH:%s"\n' "$install_dir" >> "$profile"
      fi
      echo "Added $install_dir to PATH in $profile."
    else
      echo "$install_dir is already configured in $profile."
    fi
    echo "Open a new terminal, then run: codecontext --version"
    ;;
esac
