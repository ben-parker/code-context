#!/usr/bin/env sh
# Installs the latest CodeContext release to ~/.codecontext/bin (Linux/macOS).
# Usage: curl -fsSL https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.sh | sh
set -eu

repo='ben-parker/code-context'
codecontext_home="$HOME/.codecontext"
install_dir="$codecontext_home/bin"

os=$(uname -s)
arch=$(uname -m)
case "$os" in
  Linux)  rid='linux-x64' ;;
  Darwin) if [ "$arch" = 'arm64' ]; then rid='osx-arm64'; else rid='osx-x64'; fi ;;
  *) echo "Unsupported OS: $os" >&2; exit 1 ;;
esac

echo "Resolving latest release for $rid..."
url=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
  | grep -o "\"browser_download_url\": *\"[^\"]*-$rid\.zip\"" \
  | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
[ -n "$url" ] || { echo "No asset found for $rid." >&2; exit 1; }

tmp_dir=$(mktemp -d /tmp/codecontext.XXXXXX)
tmp_zip="$tmp_dir/release.zip"
trap 'rm -rf "$tmp_dir"' EXIT HUP INT TERM
echo "Downloading $url..."
curl -fsSL -o "$tmp_zip" "$url"

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

if [ -f "$release_dir/skill/install-skill.sh" ]; then
  sh "$release_dir/skill/install-skill.sh"
fi

echo "Installed $tag to $release_dir"
case ":$PATH:" in
  *":$install_dir:"*) echo "Run: codecontext --version" ;;
  *)
    echo "Add it to your PATH, e.g.:"
    echo "  echo 'export PATH=\"\$PATH:$install_dir\"' >> ~/.profile"
    ;;
esac
