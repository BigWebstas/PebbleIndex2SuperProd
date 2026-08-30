#!/usr/bin/env bash
# Per-user install of Index2SP: copies the binary to ~/.local/bin and registers a
# desktop entry. No root required. Run ./uninstall.sh to remove.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin_dir="${XDG_BIN_HOME:-$HOME/.local/bin}"
apps_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
cfg_dir="${XDG_CONFIG_HOME:-$HOME/.config}/Index2SP"

mkdir -p "$bin_dir" "$apps_dir"
install -m 0755 "$here/Index2SP" "$bin_dir/index2sp"
sed "s|^Exec=.*|Exec=$bin_dir/index2sp|" "$here/index2sp.desktop" > "$apps_dir/index2sp.desktop"

echo "Installed: $bin_dir/index2sp"
echo "Desktop entry: $apps_dir/index2sp.desktop"
case ":$PATH:" in
  *":$bin_dir:"*) : ;;
  *) echo "NOTE: $bin_dir is not on your PATH — add it, or launch 'Index2SP' from your app menu." ;;
esac
echo
echo "First run creates $cfg_dir/config.json — edit it (tray → Edit config), then Reload config."
echo "Enable autostart from the tray menu (Start at login) or your desktop's autostart settings."
