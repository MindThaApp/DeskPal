"""Synthesizes Bloop's short interaction sounds into src/DeskPal/Assets/Sounds/ (mono, 16-bit, 22.05 kHz).

Run: python tools/sounds/make_sounds.py
Everything is built from sine sweeps, filtered noise and envelopes, so there are no recorded samples.
Each file is written to a temporary name first and only replaces the old copy of that same file.
"""
import math
import os
import random
import struct
import wave

RATE = 22050
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "DeskPal", "Assets", "Sounds")


def env(i, n, attack=0.01, release=0.6):
    t = i / RATE
    a = min(1.0, t / attack) if attack > 0 else 1.0
    r = max(0.0, 1.0 - (i / n)) ** (1.0 / max(0.05, release))
    return a * r


def sweep(dur, f0, f1, shape=1.0, vib=0.0, vib_rate=0.0, attack=0.008, release=0.5, harm=0.15):
    n = int(dur * RATE)
    out, ph = [], 0.0
    for i in range(n):
        x = i / n
        f = f0 + (f1 - f0) * (x ** shape)
        if vib:
            f *= 1 + vib * math.sin(2 * math.pi * vib_rate * i / RATE)
        ph += 2 * math.pi * f / RATE
        s = math.sin(ph) + harm * math.sin(2 * ph)
        out.append(s * env(i, n, attack, release))
    return out


def noise(dur, lp=0.2, attack=0.004, release=0.4, seed=1, bend=0.0):
    rnd = random.Random(seed)
    n = int(dur * RATE)
    out, y = [], 0.0
    for i in range(n):
        k = lp + bend * (i / n)
        y += k * (rnd.uniform(-1, 1) - y)
        out.append(y * env(i, n, attack, release) * 2.5)
    return out


def mix(*parts):
    n = max(len(p) for p, _ in parts)
    out = [0.0] * n
    for p, offset in parts:
        o = int(offset * RATE)
        for i, s in enumerate(p):
            if o + i < n:
                out[o + i] += s
            else:
                out.append(s)
    return out


def write(name, samples, peak=0.5):
    m = max(1e-6, max(abs(s) for s in samples))
    fade = int(0.004 * RATE)
    data = bytearray()
    for i, s in enumerate(samples):
        g = min(1.0, (len(samples) - i) / fade)
        data += struct.pack("<h", int(max(-1, min(1, s / m * peak * g)) * 32767))
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name + ".wav")
    tmp = path + ".tmp"
    with wave.open(tmp, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(bytes(data))
    os.replace(tmp, path)
    print(f"{name}.wav {os.path.getsize(path)} bytes")


def main():
    # Soft wet squish: a falling low blip under a little filtered noise.
    write("squish", mix((sweep(0.16, 420, 180, shape=0.6, release=0.35), 0), (noise(0.12, lp=0.08, seed=3), 0.0)))
    # Boing: a spring-like rising wobble.
    write("boing", sweep(0.42, 180, 520, shape=0.5, vib=0.12, vib_rate=18, release=0.7))
    # Giggle: three quick chirps stepping up.
    write("giggle", mix(*[(sweep(0.07, 700 + 90 * k, 950 + 90 * k, release=0.5, harm=0.3), 0.085 * k) for k in range(4)]))
    # Pat / purr: a low gentle rumble with slow amplitude flutter.
    purr = sweep(0.45, 120, 110, vib=0.03, vib_rate=6, attack=0.05, release=0.9, harm=0.4)
    purr = [s * (0.6 + 0.4 * math.sin(2 * math.pi * 24 * i / RATE)) for i, s in enumerate(purr)]
    write("purr", purr, peak=0.42)
    # Whoosh: noise whose brightness rises then falls.
    write("whoosh", noise(0.32, lp=0.03, bend=0.25, attack=0.08, release=0.5, seed=7), peak=0.4)
    # High-five clap: two short bright noise bursts with a tiny tone.
    write("clap", mix((noise(0.06, lp=0.7, seed=11, release=0.2), 0), (noise(0.05, lp=0.6, seed=12, release=0.2), 0.03),
                      (sweep(0.12, 900, 1200, release=0.3), 0.01)))
    # Hop: a quick upward blip.
    write("hop", sweep(0.14, 300, 760, shape=0.7, release=0.45))
    # Bonk: a soft hollow knock.
    write("bonk", mix((sweep(0.12, 260, 200, release=0.3, harm=0.5), 0), (noise(0.03, lp=0.4, seed=5, release=0.2), 0)))


if __name__ == "__main__":
    main()
