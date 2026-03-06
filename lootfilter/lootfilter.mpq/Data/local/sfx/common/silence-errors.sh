#!/bin/bash

# Run this from one level above your audio subfolders.
# It will recursively find and silence every .flac file found.

find . -type f -iname "*.flac" | while read -r f; do
    echo "Silencing: $f"
    dir=$(dirname "$f")
    tmp="$dir/.tmp_silent_$(basename "$f")"
    ffmpeg -loglevel error -i "$f" -af "volume=0" -y "$tmp" && mv "$tmp" "$f"
    echo "  Done: $f"
done

echo "All done."
