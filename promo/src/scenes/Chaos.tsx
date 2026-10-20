import React from "react";
import { AbsoluteFill, Easing, interpolate, random, useCurrentFrame, useVideoConfig } from "remotion";
import { Sfx } from "../components/Sfx";
import { Typed } from "../components/Type";
import type { Copy } from "../i18n/types";
import { C, F } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** When each window of the "before" lands: one a beat, then two, then four, faster and faster. */
const cutsOf = (startBeat: number, endBeat: number, B: number) => {
  const cuts: number[] = [];
  const span = endBeat - startBeat;
  let b = startBeat;
  while (b < endBeat - 0.01) {
    cuts.push(Math.round(b * B));
    const p = (b - startBeat) / span;
    b += p < 0.4 ? 1 : p < 0.75 ? 0.5 : 0.25;
  }
  return cuts;
};

const CmdWindow: React.FC<{ title: string; lines: string[]; width: number; err: boolean }> = ({ title, lines, width, err }) => (
  <div style={{ width, background: "#0c0c0c", border: "1.5px solid #3a3a3a", boxShadow: "0 20px 50px rgba(0,0,0,.7)", fontFamily: F.mono }}>
    <div style={{ display: "flex", alignItems: "center", height: 34, padding: "0 12px", background: "#1f1f1f", color: "#a8a8a8", fontSize: 17 }}>
      {title}
      <span style={{ marginLeft: "auto", letterSpacing: 18 }}>— ▢ ✕</span>
    </div>
    <div style={{ padding: "14px 16px 18px", fontSize: 21, lineHeight: 1.55, color: "#d0d0d0" }}>
      {lines.map((l, i) => (
        <div key={i} style={{ color: err && i === lines.length - 1 ? C.err : undefined, whiteSpace: "nowrap", overflow: "hidden" }}>
          {l}
        </div>
      ))}
    </div>
  </div>
);

export const Chaos: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { fps, width, height } = useVideoConfig();
  const portrait = height > width;
  const qBeats = beats >= 16 ? 4 : beats >= 8 ? 2.5 : 1.5;
  const giveUp = beats >= 16;
  const endBeat = giveUp ? beats - 1.5 : beats;
  const cuts = cutsOf(qBeats, endBeat, B);
  const cps = copy.chaos.question.length / ((qBeats * B * 0.6) / fps);

  const current = cuts.filter((c) => c <= frame).length - 1;
  const sinceCut = current >= 0 ? frame - cuts[current] : 99;
  const shake = sinceCut < 3 ? (3 - sinceCut) * 7 : 0;
  const glitch = sinceCut < 2;
  const fadeAll = giveUp ? interpolate(frame, [endBeat * B, endBeat * B + 8], [1, 0.22], clamp) : 1;
  const questionOut = interpolate(frame, [qBeats * B - 2, qBeats * B + 3], [1, 0], clamp);
  const winW = portrait ? 860 : 820;

  return (
    <AbsoluteFill style={{ background: "#050505", overflow: "hidden" }}>
      {/* The question, typed on a black screen */}
      <AbsoluteFill style={{ display: "grid", placeItems: "center", opacity: questionOut, padding: portrait ? 80 : 160 }}>
        <Typed
          text={copy.chaos.question}
          at={2}
          cps={cps}
          fps={fps}
          style={{ fontFamily: F.mono, fontWeight: 700, fontSize: portrait ? 64 : 76, color: C.ink, lineHeight: 1.25, textAlign: "center" }}
        />
      </AbsoluteFill>
      {Array.from({ length: Math.min(copy.chaos.question.length, Math.floor((qBeats * B) / 2)) }, (_, i) => (
        <Sfx key={`k${i}`} at={2 + i * 2} name="key" volume={0.5} />
      ))}

      {/* The pile of windows */}
      <AbsoluteFill style={{ translate: `${(random(`sx${current}`) - 0.5) * shake}px ${(random(`sy${current}`) - 0.5) * shake}px`, opacity: fadeAll }}>
        {cuts.map((at, i) => {
          if (frame < at) return null;
          const w = copy.chaos.windows[i % copy.chaos.windows.length];
          const x = (portrait ? 40 : 60) + random(`x${i}`) * (width - winW - (portrait ? 80 : 120));
          const y = (portrait ? 200 : 60) + random(`y${i}`) * (height - (portrait ? 520 : 320));
          const pop = interpolate(frame - at, [0, 3], [0.82, 1], { ...clamp, easing: Easing.out(Easing.back(2)) });
          return (
            <div key={i} style={{ position: "absolute", left: x, top: y, rotate: `${(random(`r${i}`) - 0.5) * 9}deg`, scale: String(pop) }}>
              <CmdWindow title={w.title} lines={w.lines} width={winW} err />
            </div>
          );
        })}
      </AbsoluteFill>
      {cuts.map((at, i) => (
        <Sfx key={`c${i}`} at={at} name={i % 2 ? "glitch" : "error"} volume={0.55} />
      ))}

      {/* A word stamped over each cut, with the colour fringes of a broken signal */}
      {current >= 0 && frame < endBeat * B && (
        <AbsoluteFill style={{ display: "grid", placeItems: "center", pointerEvents: "none" }}>
          <div
            style={{
              fontFamily: F.display,
              fontWeight: 900,
              fontStretch: "125%",
              fontSize: portrait ? 190 : 230,
              lineHeight: 1,
              color: "#fff",
              textTransform: "uppercase",
              letterSpacing: "-0.02em",
              textShadow: glitch ? `-10px 0 ${C.err}, 10px 0 #00d5ff` : `0 10px 40px rgba(0,0,0,.8)`,
              rotate: `${(random(`sr${current}`) - 0.5) * 8}deg`,
              scale: String(interpolate(sinceCut, [0, 4], [1.25, 1], { ...clamp, easing: Easing.out(Easing.cubic) })),
            }}
          >
            {copy.chaos.stamps[current % copy.chaos.stamps.length]}
          </div>
        </AbsoluteFill>
      )}

      {/* Torn scanlines on the cuts */}
      {glitch &&
        [0, 1, 2, 3, 4].map((k) => (
          <div
            key={k}
            style={{
              position: "absolute",
              left: 0,
              right: 0,
              top: random(`g${current}-${k}`) * height,
              height: 6 + random(`gh${current}-${k}`) * 40,
              background: k % 2 ? "rgba(255,95,86,.35)" : "rgba(255,255,255,.18)",
              translate: `${(random(`gx${current}-${k}`) - 0.5) * 120}px 0`,
            }}
          />
        ))}

      {giveUp && (
        <AbsoluteFill style={{ display: "grid", placeItems: "center" }}>
          <div
            style={{
              fontFamily: F.display,
              fontWeight: 800,
              fontSize: 84,
              color: C.ink,
              opacity: interpolate(frame, [endBeat * B + 2, endBeat * B + 8], [0, 1], clamp),
              translate: `0 ${interpolate(frame, [endBeat * B + 2, endBeat * B + 10], [24, 0], clamp)}px`,
            }}
          >
            {copy.chaos.giveUp}
          </div>
        </AbsoluteFill>
      )}
    </AbsoluteFill>
  );
};
