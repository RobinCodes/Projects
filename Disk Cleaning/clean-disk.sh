#!/usr/bin/env bash
#
# clean-disk.sh — thorough disk cleaner for Arch Linux.
#
# Arch doesn't accumulate cruft the way Windows does (no WinSxS, no
# Windows.old, no monthly cumulative-update bloat), but three things do
# grow unbounded if left alone: the pacman package cache, the systemd
# journal, and orphaned/AUR-helper caches. This covers those plus the
# usual user-level junk (trash, thumbnails, browser/app caches).
#
# Safe by default: package cache keeps the last 3 versions of each
# package (so you can downgrade), journal is vacuumed to 2 weeks, and
# anything that discards otherwise-irrecoverable state (full pacman
# cache wipe, docker prune, language-tool build caches) is opt-in via
# --aggressive.

set -euo pipefail

DRY_RUN=0
ASSUME_YES=0
AGGRESSIVE=0
USER_ONLY=0
TOTAL_FREED=0

usage() {
    cat <<'EOF'
Usage: clean-disk.sh [options]

  --dry-run       Show what would be removed / how much space would be freed; delete nothing.
  --yes           Don't prompt for confirmation on destructive steps.
  --aggressive    Also: full pacman cache wipe (pacman -Scc, no downgrade safety net),
                  docker system prune, and dev tool caches (npm/pip/cargo/go).
  --user-only     Skip everything that needs root (pacman cache, orphans, journal, /var/tmp).
  -h, --help      Show this help.
EOF
}

for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY_RUN=1 ;;
        --yes) ASSUME_YES=1 ;;
        --aggressive) AGGRESSIVE=1 ;;
        --user-only) USER_ONLY=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; usage; exit 1 ;;
    esac
done

IS_ROOT=0
[[ $EUID -eq 0 ]] && IS_ROOT=1

if [[ $IS_ROOT -eq 0 && $USER_ONLY -eq 0 ]]; then
    echo "Note: not running as root — system-wide steps (pacman cache, orphans, journal," >&2
    echo "coredumps, /var/tmp) will be skipped. Re-run with sudo for those, or pass --user-only" >&2
    echo "to silence this note." >&2
fi

color() { local c=$1; shift; printf '\033[%sm%s\033[0m\n' "$c" "$*"; }
info()  { color '36' "$*"; }
ok()    { color '32' "$*"; }
warn()  { color '33' "$*" >&2; }

confirm() {
    [[ $ASSUME_YES -eq 1 || $DRY_RUN -eq 1 ]] && return 0
    read -r -p "$1 [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]]
}

human() {
    local bytes=$1
    numfmt --to=iec --suffix=B "$bytes" 2>/dev/null || echo "${bytes}B"
}

# du in KB (1024-byte blocks) summed, in bytes
path_size() {
    local p=$1
    [[ -e "$p" ]] || { echo 0; return; }
    du -sk --apparent-size "$p" 2>/dev/null | awk '{print $1 * 1024}'
}

declare -A RESULT_FREED

record() {
    local name=$1 freed=$2
    RESULT_FREED["$name"]=$freed
    TOTAL_FREED=$(( TOTAL_FREED + freed ))
}

clean_path() {
    local name=$1 path=$2
    if [[ ! -e "$path" ]]; then
        info "  [skip] $name — not present"
        return
    fi
    local before after freed
    before=$(path_size "$path")
    if [[ $before -eq 0 ]]; then
        info "  [skip] $name — already empty"
        return
    fi
    info "  [clean] $name — $(human "$before") ($path)"
    if [[ $DRY_RUN -eq 0 ]]; then
        find "$path" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + 2>/dev/null || true
        after=$(path_size "$path")
        freed=$(( before - after ))
    else
        freed=$before
    fi
    record "$name" "$freed"
}

have() { command -v "$1" &>/dev/null; }

echo
ok "=== Arch Linux Disk Cleaner ==="
[[ $DRY_RUN -eq 1 ]] && warn "DRY RUN — nothing will be deleted"
echo

ROOT_FS_BEFORE=$(df --output=avail -B1 / | tail -1 | tr -d ' ')

# --- pacman package cache ---------------------------------------------

if [[ $IS_ROOT -eq 1 && $USER_ONLY -eq 0 ]]; then
    info "-- pacman package cache --"
    if have paccache; then
        if [[ $AGGRESSIVE -eq 1 ]]; then
            if confirm "Aggressive: wipe ALL cached package versions (pacman -Scc)? You lose the ability to downgrade."; then
                before=$(path_size /var/cache/pacman/pkg)
                if [[ $DRY_RUN -eq 0 ]]; then
                    pacman -Scc --noconfirm >/dev/null
                    after=$(path_size /var/cache/pacman/pkg)
                    record "pacman cache (full wipe)" $(( before - after ))
                else
                    record "pacman cache (full wipe)" "$before"
                fi
                info "  [clean] pacman cache — full wipe"
            fi
        else
            before=$(path_size /var/cache/pacman/pkg)
            if [[ $DRY_RUN -eq 0 ]]; then
                paccache -r -k 3 >/dev/null 2>&1 || true
                paccache -r -u -k 0 >/dev/null 2>&1 || true
                after=$(path_size /var/cache/pacman/pkg)
                record "pacman cache (keep last 3)" $(( before - after ))
            else
                # estimate: paccache -d only lists what it would remove
                est=$(paccache -d -k 3 2>/dev/null | wc -l || echo 0)
                record "pacman cache (keep last 3, ~$est files)" 0
            fi
            info "  [clean] pacman cache — trimmed to last 3 versions per package"
        fi
    else
        warn "  [skip] pacman-contrib not installed — install it for 'paccache' (safe cache trimming)."
        info   "         Falling back to 'pacman -Sc' (removes uninstalled packages' cache only)."
        before=$(path_size /var/cache/pacman/pkg)
        if [[ $DRY_RUN -eq 0 ]]; then
            pacman -Sc --noconfirm >/dev/null
            after=$(path_size /var/cache/pacman/pkg)
            record "pacman cache (uninstalled only)" $(( before - after ))
        else
            record "pacman cache (uninstalled only)" "$before"
        fi
    fi
    echo
fi

# --- orphaned packages ---------------------------------------------------

if [[ $IS_ROOT -eq 1 && $USER_ONLY -eq 0 ]]; then
    info "-- orphaned packages --"
    mapfile -t orphans < <(pacman -Qtdq 2>/dev/null || true)
    if [[ ${#orphans[@]} -eq 0 ]]; then
        info "  [skip] no orphaned packages"
    else
        echo "  Found ${#orphans[@]} orphaned package(s): ${orphans[*]}"
        if confirm "Remove them with pacman -Rns?"; then
            if [[ $DRY_RUN -eq 0 ]]; then
                pacman -Rns --noconfirm "${orphans[@]}" >/dev/null
            fi
            ok "  removed ${#orphans[@]} orphaned package(s)"
        fi
    fi
    echo
fi

# --- systemd journal -------------------------------------------------------

if [[ $IS_ROOT -eq 1 && $USER_ONLY -eq 0 ]]; then
    info "-- systemd journal --"
    if have journalctl; then
        before=$(path_size /var/log/journal)
        info "  [clean] journal — $(human "$before"), vacuuming to 2 weeks / 200M"
        if [[ $DRY_RUN -eq 0 ]]; then
            journalctl --vacuum-time=2weeks >/dev/null 2>&1 || true
            journalctl --vacuum-size=200M >/dev/null 2>&1 || true
            after=$(path_size /var/log/journal)
            record "systemd journal" $(( before - after ))
        else
            record "systemd journal" 0
        fi
    fi
    echo
fi

# --- coredumps & /var/tmp --------------------------------------------------

if [[ $IS_ROOT -eq 1 && $USER_ONLY -eq 0 ]]; then
    info "-- coredumps & /var/tmp --"
    clean_path "systemd coredumps" /var/lib/systemd/coredump
    if [[ -d /var/tmp ]]; then
        before=$(find /var/tmp -mindepth 1 -atime +9 2>/dev/null | wc -l)
        if [[ $before -gt 0 ]]; then
            info "  [clean] /var/tmp — $before item(s) untouched for 10+ days"
            if [[ $DRY_RUN -eq 0 ]]; then
                find /var/tmp -mindepth 1 -atime +9 -exec rm -rf -- {} + 2>/dev/null || true
            fi
        else
            info "  [skip] /var/tmp — nothing older than 10 days"
        fi
    fi
    echo
fi

# --- user-level caches (no root needed) ------------------------------------

info "-- user cache & trash --"
clean_path "Thumbnail cache" "$HOME/.cache/thumbnails"
clean_path "Trash" "$HOME/.local/share/Trash"
clean_path "yay cache" "$HOME/.cache/yay"
clean_path "paru cache" "$HOME/.cache/paru"

# Generic ~/.cache: trim entries not accessed in 30+ days rather than wiping
# the whole thing (some apps, e.g. pip/pre-commit, treat ~/.cache as a real
# store, not scratch space).
if [[ -d "$HOME/.cache" ]]; then
    before=$(find "$HOME/.cache" -mindepth 1 -atime +30 2>/dev/null | wc -l)
    if [[ $before -gt 0 ]]; then
        info "  [clean] ~/.cache — $before item(s) untouched for 30+ days"
        if [[ $DRY_RUN -eq 0 ]]; then
            find "$HOME/.cache" -mindepth 1 -atime +30 -exec rm -rf -- {} + 2>/dev/null || true
        fi
    else
        info "  [skip] ~/.cache — nothing stale"
    fi
fi
echo

# --- flatpak -----------------------------------------------------------

if have flatpak; then
    info "-- flatpak --"
    if [[ $DRY_RUN -eq 0 ]]; then
        flatpak uninstall --unused --noninteractive >/dev/null 2>&1 || true
        ok "  removed unused flatpak runtimes"
    else
        info "  [would run] flatpak uninstall --unused"
    fi
    echo
fi

# --- aggressive: docker + dev tool caches ----------------------------------

if [[ $AGGRESSIVE -eq 1 ]]; then
    if have docker && docker info &>/dev/null; then
        info "-- docker (aggressive) --"
        if confirm "Run 'docker system prune -af --volumes'? This deletes ALL stopped containers, unused images, and unused volumes."; then
            if [[ $DRY_RUN -eq 0 ]]; then
                docker system prune -af --volumes >/dev/null
            fi
            ok "  docker pruned"
        fi
        echo
    fi

    info "-- dev tool caches (aggressive) --"
    have npm  && { info "  [clean] npm cache";  [[ $DRY_RUN -eq 0 ]] && npm cache clean --force >/dev/null 2>&1 || true; }
    have pip  && { info "  [clean] pip cache";  [[ $DRY_RUN -eq 0 ]] && pip cache purge >/dev/null 2>&1 || true; }
    have go   && { info "  [clean] go build cache"; [[ $DRY_RUN -eq 0 ]] && go clean -cache >/dev/null 2>&1 || true; }
    clean_path "cargo registry cache" "$HOME/.cargo/registry/cache"
    echo
fi

# --- Summary -----------------------------------------------------------

ok "=== Summary ==="
for name in "${!RESULT_FREED[@]}"; do
    freed=${RESULT_FREED[$name]}
    [[ $freed -gt 0 ]] && printf '  %-40s %s\n' "$name" "$(human "$freed")"
done

label="Freed"
[[ $DRY_RUN -eq 1 ]] && label="Would free"
echo
ok "$label total (measurable items): $(human "$TOTAL_FREED")"

if [[ $DRY_RUN -eq 0 ]]; then
    ROOT_FS_AFTER=$(df --output=avail -B1 / | tail -1 | tr -d ' ')
    echo "Free space on /: before $(human "$ROOT_FS_BEFORE") -> after $(human "$ROOT_FS_AFTER")"
fi

if [[ $AGGRESSIVE -eq 0 ]]; then
    echo
    color '90' "Tip: pass --aggressive for a full pacman cache wipe + docker prune + dev tool caches."
fi
if [[ $IS_ROOT -eq 0 && $USER_ONLY -eq 0 ]]; then
    color '90' "Tip: re-run with sudo to also clean the pacman cache, orphans, journal, and coredumps."
fi
