#!/usr/bin/env python3
"""
Renders the original's sound effects as OGG files in
src/EliteSharp/Assets/Sounds, which the game plays.

The original makes its sound effects on the BBC Master's SN76489 sound chip,
from four tables of bytes (SFXPR, SFXBT, SFXFQ and SFXVC), which NOISE copies
into the sound buffer, and SOINT sends to the sound chip 50 times a second,
changing each sound's frequency and fading it out until it ends. This runs
each effect through an emulation of SOINT and the sound chip, at the loudest
volume setting (the game turns the volume down as the volume setting does on
the chip, in 2 dB steps), and encodes it with ffmpeg (which must be on the
path, with libvorbis).

Each effect plays on its own, from the first run of SOINT after it starts to
the run that ends it, so its length is a whole number of 50ths of a second.
The tables also say which voice each effect uses and its priority, which the
game has in its code (Sound/SoundEffects.cs), and the tests check against the
original bytes.

Usage: python tools/render_sounds.py   (run from the repository root)
"""

import math
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import original  # noqa: E402

OUT_DIR = os.path.join(original.ROOT, "src", "EliteSharp", "Assets", "Sounds")

# The effects' file names, by effect number (the names are the original's,
# such as soboop, where it has them)
NAMES = [
    "boop",          # soboop: a long, low beep
    "beep",          # sobeep: a short, high beep
    "click",         # soclick: a click
    "laser",         # solaser: our laser firing (first part)
    "explosion",     # soexpl: an explosion
    "laser-2",       # solas2: a laser firing, ours or an enemy's (second part)
    "hit",           # sohit: a laser strike on another ship, or the energy bomb
    "ecm",           # soecm: the E.C.M.
    "launch",        # solaun: launching, docking or a missile launch
    "hit-us",        # (9): us being hit by lasers (first part)
    "hyperspace",    # sohyp: hyperspace (first part)
    "hyperspace-2",  # sohyp2: hyperspace (second part)
]

SAMPLE_RATE = 44100
SAMPLES_PER_TICK = SAMPLE_RATE // 50
CHIP_RATE = 4_000_000.0 / 16.0
LOUDEST = 7  # the volume setting the effects are rendered at

# SOFH and SOOFF: the sound chip latch bytes for each voice's frequency and
# for silencing each voice (voice 0 is the noise channel)
FREQUENCY_LATCH = [0b11000000, 0b10100000, 0b10000000]
VOLUME_LATCH = [0b11111111, 0b10111111, 0b10011111]

# The output level for each attenuation value (2 dB steps, and 15 is silent)
VOLUME_TABLE = [10 ** (-2.0 * i / 20) for i in range(15)] + [0.0]


class Chip:
    """The SN76489 sound chip (three tone channels and a noise channel)."""

    def __init__(self):
        self.tone_period = [1024, 1024, 1024]
        self.attenuation = [15, 15, 15, 15]
        self.noise_control = 0
        self.latched = 0
        self.tone_counter = [0.0, 0.0, 0.0]
        self.tone_output = [False, False, False]
        self.noise_counter = 0.0
        self.lfsr = 0x4000
        self.noise_output = False

    def write(self, value):
        if value & 0x80:
            self.latched = (value >> 4) & 7
            data = value & 15
            channel = self.latched >> 1
            if self.latched & 1:
                self.attenuation[channel] = data
            elif channel == 3:
                self.noise_control = data
                self.lfsr = 0x4000
            else:
                self.tone_period[channel] = (self.tone_period[channel] & 0x3F0) | data
        else:
            channel = self.latched >> 1
            if (self.latched & 1) == 0 and channel < 3:
                self.tone_period[channel] = (self.tone_period[channel] & 15) | ((value & 0x3F) << 4)

    def clock_noise(self):
        white = (self.noise_control & 4) != 0
        feedback = ((self.lfsr ^ (self.lfsr >> 1)) & 1) if white else (self.lfsr & 1)
        self.lfsr = (self.lfsr >> 1) | (feedback << 14)
        self.noise_output = (self.lfsr & 1) != 0

    def sample(self):
        step = CHIP_RATE / SAMPLE_RATE
        mix = 0.0
        for channel in range(3):
            period = self.tone_period[channel] or 1024
            self.tone_counter[channel] -= step
            while self.tone_counter[channel] <= 0:
                self.tone_counter[channel] += period
                self.tone_output[channel] = not self.tone_output[channel]
                if channel == 2 and (self.noise_control & 3) == 3 and self.tone_output[channel]:
                    self.clock_noise()

            # Very high frequencies are output as a constant level
            level = 1 if period <= 1 else (1 if self.tone_output[channel] else -1)
            mix += level * VOLUME_TABLE[self.attenuation[channel]]

        if (self.noise_control & 3) != 3:
            noise_period = 16 << (self.noise_control & 3)
            self.noise_counter -= step
            while self.noise_counter <= 0:
                self.noise_counter += noise_period
                self.clock_noise()

        mix += (1 if self.noise_output else -1) * VOLUME_TABLE[self.attenuation[3]]
        return max(-32768, min(32767, int(mix * 0.22 * 32767)))


def render(effect, priority, bits, frequency, volume_change):
    """Render an effect as 16-bit samples, as NOISE and SOINT make it on its voice."""
    # SOFLUSH: silence all the channels, and set the noise channel to white
    # noise at the frequency of tone channel 2, which is where voice 0 sets
    # its frequency
    chip = Chip()
    for value in VOLUME_LATCH + [0b11011111, 0b11101111]:
        chip.write(value)

    # NOISE: voice 0 (the noise channel) if bit 0 of SFXBT is set, otherwise a
    # tone voice (which one doesn't change the sound)
    y = 0 if bits & 1 else 2
    flag = 0x80
    volume = (priority >> 1) & 7
    count = bits
    frequency_change = (bits & 15) >> 1

    samples = []
    while True:
        # SOINT, for this voice
        delta = 0 if flag & 0x80 else (frequency_change if frequency_change else -1)
        if delta >= 0:
            frequency = (frequency + delta) & 0xFF
            chip.write(((frequency << 2) & 15) | FREQUENCY_LATCH[y])
            chip.write(frequency >> 2)

        if flag & 0x80:
            flag >>= 1
            chip.write(((volume + LOUDEST) & 0xFF) ^ VOLUME_LATCH[y])
        else:
            count = (count - 1) & 0xFF
            if count == 0:
                break
            if (count & volume_change) == 0:
                volume = (volume - 1) & 0xFF
                if volume == 0:
                    break
                chip.write(((volume + LOUDEST) & 0xFF) ^ VOLUME_LATCH[y])

        samples.extend(chip.sample() for _ in range(SAMPLES_PER_TICK))

    return samples


def write_ogg(path, samples):
    with tempfile.TemporaryDirectory() as folder:
        wav = os.path.join(folder, "sound.wav")
        data = struct.pack(f"<{len(samples)}h", *samples)
        with open(wav, "wb") as f:
            f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE")
            f.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, SAMPLE_RATE, SAMPLE_RATE * 2, 2, 16))
            f.write(b"data" + struct.pack("<I", len(data)) + data)
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", wav, "-c:a", "libvorbis", "-q:a", "6",
                        "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", path],
                       check=True)


def main():
    tables = [original.code_bytes(label, 12) for label in ("SFXPR", "SFXBT", "SFXFQ", "SFXVC")]
    os.makedirs(OUT_DIR, exist_ok=True)
    for effect, name in enumerate(NAMES):
        priority, bits, frequency, volume_change = (table[effect] for table in tables)
        samples = render(effect, priority, bits, frequency, volume_change)
        write_ogg(os.path.join(OUT_DIR, f"{name}.ogg"), samples)
        print(f"{effect:2} {name:13} {len(samples) / SAMPLE_RATE:5.2f}s  "
              f"priority {priority:3}, {'noise' if bits & 1 else 'tone'}")


if __name__ == "__main__":
    main()
