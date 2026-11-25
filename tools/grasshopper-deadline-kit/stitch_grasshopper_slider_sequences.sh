#!/usr/bin/env bash
set -euo pipefail

SRC="${1:-${GH_ANIMATION_SRC:-.}}"
OUT="${2:-${GH_ANIMATION_OUT:-"$SRC/_stitched"}}"
FFMPEG="${FFMPEG:-ffmpeg}"

read -r -a STEMS <<< "${GH_STEMS:-GH_SLIDER_PRACTICE_9 GH_SLIDER_PRACTICE_10 GH_SLIDER_PRACTICE_11}"
IFS="|" read -r -a LABELS <<< "${GH_LABELS:-Depth range|Width adjustment|Height adjustment}"
read -r -a SKIP_COUNTS <<< "${GH_SKIP_COUNTS:-0 0 0}"

FPS="${GH_FPS:-2}"
GIF_FPS="${GH_GIF_FPS:-4}"
GIF_WIDTH="${GH_GIF_WIDTH:-960}"
CONTACT_TILE="${GH_CONTACT_TILE:-6x4}"

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd -P)"

TMP="$OUT/_combined_frames"
SEGMENTS="$OUT/_segments"
CONCAT="$OUT/concat.txt"

LOGS="$OUT/_logs"
mkdir -p "$TMP" "$SEGMENTS" "$LOGS"
rm -f "$TMP"/frame_*.bmp "$SEGMENTS"/*.mp4 "$CONCAT"

frame_index=0
segment_index=0
has_drawtext=0

if "$FFMPEG" -hide_banner -filters 2>/dev/null | grep -q " drawtext "; then
  has_drawtext=1
else
  echo "ffmpeg drawtext filter not found; labeled outputs will omit text overlays." >&2
fi

run_ffmpeg() {
  local log="$LOGS/$1.log"
  shift
  if "$FFMPEG" "$@" >"$log" 2>&1; then
    return 0
  else
    local status=$?
    echo "ffmpeg failed (exit $status). Log: $log" >&2
    tail -n 30 "$log" >&2
    return "$status"
  fi
}

copy_sequence() {
  local stem="$1"
  local skip_count="${2:-0}"
  local label="${3:-$stem}"
  local local_index=0
  local copied=0
  local segment_dir="$SEGMENTS/$stem"

  mkdir -p "$segment_dir"
  rm -f "$segment_dir"/frame_*.bmp

  shopt -s nullglob
  local files=("$SRC"/"$stem"_*.bmp "$SRC"/"$stem"_*.png "$SRC"/"$stem"_*.jpg "$SRC"/"$stem"_*.jpeg)
  shopt -u nullglob

  if [ "${#files[@]}" -eq 0 ]; then
    echo "Skipping $stem: no frames found" >&2
    return 0
  fi

  IFS=$'\n' files=($(printf "%s\n" "${files[@]}" | sort))
  unset IFS

  local f
  for f in "${files[@]}"; do
    if [ "$local_index" -lt "$skip_count" ]; then
      local_index=$((local_index + 1))
      continue
    fi

    local name
    name="$(printf "frame_%03d.bmp" "$copied")"
    run_ffmpeg "convert_${segment_index}_${copied}" -y -i "$f" \
      -frames:v 1 -pix_fmt bgr24 "$segment_dir/$name"
    cp -f "$segment_dir/$name" "$TMP/$(printf "frame_%03d.bmp" "$frame_index")"
    frame_index=$((frame_index + 1))
    copied=$((copied + 1))
    local_index=$((local_index + 1))
  done

  if [ "$copied" -eq 0 ]; then
    echo "Skipping $stem: all frames skipped" >&2
    return 0
  fi

  local clip="$SEGMENTS/$(printf "%02d" "$segment_index")_${stem}.mp4"
  local vf="scale=1280:-2:flags=lanczos,format=yuv420p"

  if [ "$has_drawtext" -eq 1 ]; then
    vf="scale=1280:-2:flags=lanczos,drawbox=x=0:y=0:w=iw:h=70:color=black@0.45:t=fill,drawtext=text='${label}':x=34:y=24:fontsize=28:fontcolor=white,format=yuv420p"
  fi

  run_ffmpeg "segment_$segment_index" -y -framerate "$FPS" \
    -i "$segment_dir/frame_%03d.bmp" \
    -vf "$vf" \
    -an "$clip"

  printf "file '%s'\n" "$clip" >> "$CONCAT"
  echo "Added $stem ($copied frames) as '$label'"
  segment_index=$((segment_index + 1))
}

for i in "${!STEMS[@]}"; do
  copy_sequence "${STEMS[$i]}" "${SKIP_COUNTS[$i]:-0}" "${LABELS[$i]:-${STEMS[$i]}}"
done

if [ "$frame_index" -eq 0 ]; then
  echo "No frames were found in $SRC" >&2
  exit 1
fi

run_ffmpeg clean_mp4 -y -framerate "$FPS" \
  -i "$TMP/frame_%03d.bmp" \
  -vf "scale=1280:-2:flags=lanczos,format=yuv420p" \
  -an "$OUT/grasshopper_slider_loop.mp4"

run_ffmpeg clean_gif -y -i "$OUT/grasshopper_slider_loop.mp4" \
  -vf "fps=$GIF_FPS,scale=$GIF_WIDTH:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=160[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5" \
  -loop 0 "$OUT/grasshopper_slider_loop.gif"

run_ffmpeg labeled_mp4 -y -f concat -safe 0 -i "$CONCAT" \
  -c copy "$OUT/grasshopper_slider_loop_labeled.mp4"

run_ffmpeg labeled_gif -y -i "$OUT/grasshopper_slider_loop_labeled.mp4" \
  -vf "fps=$GIF_FPS,scale=$GIF_WIDTH:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=160[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5" \
  -loop 0 "$OUT/grasshopper_slider_loop_labeled.gif"

run_ffmpeg contact -y -pattern_type glob -i "$TMP/frame_*.bmp" \
  -vf "scale=240:135,tile=$CONTACT_TILE" \
  -frames:v 1 "$OUT/grasshopper_slider_contact_sheet.png"

echo "Done:"
echo "$OUT/grasshopper_slider_loop.mp4"
echo "$OUT/grasshopper_slider_loop.gif"
echo "$OUT/grasshopper_slider_loop_labeled.mp4"
echo "$OUT/grasshopper_slider_loop_labeled.gif"
echo "$OUT/grasshopper_slider_contact_sheet.png"
