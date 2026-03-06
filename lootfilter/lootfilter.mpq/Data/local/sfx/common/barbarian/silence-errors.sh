#!/bin/bash

FILES=(
    "bar_cannot.flac"
    "bar_cantcarry.flac"
    "bar_cant.flac"
    "bar_cantuse.flac"
    "bar_mana.flac"
    "bar_moremana.flac"
    "bar_needmana.flac"
    "bar_notenoughmana.flac"
    "bar_nothere.flac"
    "bar_overburdened.flac"
    "bar_toomuch.flac"
)

for f in "${FILES[@]}"; do
    if [[ -f "$f" ]]; then
        echo "Silencing: $f"
        ffmpeg -i "$f" -af "volume=0" -y "tmp_silent_$f" && mv "tmp_silent_$f" "$f"
        echo "  Done: $f"
    else
        echo "  SKIPPED (not found): $f"
    fi
done

echo "All done."
