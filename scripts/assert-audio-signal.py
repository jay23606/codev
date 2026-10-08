#!/usr/bin/env python3
"""Fail if the captured screen-reader focus event produced a silent WAV."""

import math
import argparse
import struct
import wave


def main(path: str, skip_seconds: float) -> None:
    try:
        with wave.open(path, "rb") as audio:
            channels = audio.getnchannels()
            sample_width = audio.getsampwidth()
            sample_rate = audio.getframerate()
            frames = audio.readframes(audio.getnframes())
    except (OSError, wave.Error) as error:
        raise SystemExit(f"Could not read screen-reader audio capture: {error}") from error

    if channels != 1 or sample_width != 2 or sample_rate != 22050:
        raise SystemExit(
            "Unexpected screen-reader capture format: "
            f"channels={channels}, sample_width={sample_width}, rate={sample_rate}"
        )

    all_samples = [sample[0] for sample in struct.iter_unpack("<h", frames)]
    samples = all_samples[int(sample_rate * skip_seconds) :]
    if len(samples) < sample_rate // 2:
        raise SystemExit(f"Screen-reader audio capture was too short: {len(samples)} samples")

    nonzero = sum(sample != 0 for sample in samples)
    rms = math.sqrt(sum(sample * sample for sample in samples) / len(samples))
    peak = max((abs(sample) for sample in samples), default=0)
    if nonzero < 1000 or rms < 8 or peak < 64:
        raise SystemExit(
            "Orca processed the focus event but produced no measurable speech audio: "
            f"nonzero_samples={nonzero}, rms={rms:.2f}, peak={peak}"
        )
    print(
        "Orca produced non-silent Speech Dispatcher audio for the Search focus event: "
        f"nonzero_samples={nonzero}, rms={rms:.2f}, peak={peak}"
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("wav", help="Path to the captured screen-reader WAV")
    parser.add_argument("--skip-seconds", type=float, default=0)
    arguments = parser.parse_args()
    if arguments.skip_seconds < 0:
        parser.error("--skip-seconds must be non-negative")
    main(arguments.wav, arguments.skip_seconds)
