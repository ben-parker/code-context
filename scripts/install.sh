#!/usr/bin/env sh
# Installs the latest CodeContext release to ~/.codecontext/bin (Linux/macOS).
# Usage: curl -fsSL https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.sh | sh
set -eu

repo='ben-parker/code-context'
codecontext_home="$HOME/.codecontext"
install_dir="$codecontext_home/bin"

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
trap 'rm -rf "$tmp_dir"' EXIT HUP INT TERM
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
