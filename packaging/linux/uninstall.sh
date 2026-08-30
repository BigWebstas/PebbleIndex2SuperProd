#!/usr/bin/env bash
# Removes what install.sh created. Leaves your config in ~/.config/Index2SP.
set -euo pipefail

bin_dir="${XDG_BIN_HOME:-$HOME/.local/bin}"
apps_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/index2sp.desktop"

rm -fv "$bin_dir/index2sp" "$apps_dir/index2sp.desktop" "$autostart"
echo "Config left in ${XDG_CONFIG_HOME:-$HOME/.config}/Index2SP (delete it manually if you want)."
