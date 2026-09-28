# Media fixtures

Recorded 2026-09-29 inside `ghcr.io/wulfftech/wondarr:develop` (ffmpeg/ffprobe 9.0.2, fpcalc 1.6.1) on `ch01`.

| File | Made with |
|---|---|
| `tone-320.mp3` | 3 s stereo 440 Hz + 660 Hz, `libmp3lame -b:a 320k`, 44.1 kHz |
| `tone-v0.mp3` | 3 s 440 Hz, `libmp3lame -q:a 0` (VBR V0) |
| `tone.flac` | 3 s 440 Hz, FLAC 16-bit 44.1 kHz |
| `tone-24.flac` | 1 s 440 Hz, FLAC `s32` (24 bits per raw sample) 96 kHz |
| `tone-256.m4a` | 3 s 440 Hz, native `aac -b:a 256k` |
| `tone-160.opus` | 3 s 440 Hz, `libopus -b:a 160k`, 48 kHz |
| `garbage.mp3` | 20 000 random bytes |

`<file>.ffprobe.json` is `ffprobe -v error -print_format json -show_format -show_streams <file>`; `<file>.fpcalc.json` is `fpcalc -json <file>`. For `garbage.mp3`, ffprobe exited 1 with `{}` on stdout and fpcalc exited 2; their stderr is in the `.stderr` files.

Observed: `fpcalc -json -` reading WAV from stdin (`ffmpeg -i f -f wav - | fpcalc -json -`) gives the same fingerprint as the file but `"duration": 0.00`; a window shorter than ~3 s of a pure tone gives `ERROR: Empty fingerprint` (exit 2). ffprobe reports `bits_per_sample` 0 for MP3/AAC/Opus and for FLAC `s32`; the 24-bit depth is in `bits_per_raw_sample` (a string).
