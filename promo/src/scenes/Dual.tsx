import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { Sfx } from "../components/Sfx";
import { BlockText, Rise } from "../components/Type";
import { tex, Tiles } from "../components/World";
import type { Copy } from "../i18n/types";
import { C, F } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** One program, two kinds of people: the beginner's checklist and the veteran's console. */
export const Dual: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const half = beats / 2;
  const noobAt = (i: number) => Math.round(B * (0.8 + i * (half - 1) / 3));
  const proAt = (i: number) => Math.round(B * (half + 0.6 + i * (half - 1) / 3));
  const split = interpolate(frame, [0, 8], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });
  const proIn = interpolate(frame, [Math.round(half * B) - 4, Math.round(half * B) + 4], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });

  const box: React.CSSProperties = portrait
    ? { position: "absolute", left: 0, right: 0, height: height / 2, display: "flex", flexDirection: "column", justifyContent: "center", gap: 36, padding: "0 90px" }
    : { position: "absolute", top: 0, bottom: 0, width: width / 2, display: "flex", flexDirection: "column", justifyContent: "center", gap: 40, padding: "0 110px" };

  return (
    <AbsoluteFill style={{ background: C.terminal }}>
      {/* The beginner's half: the grass side */}
      <div
        style={{
          ...box,
          ...(portrait ? { top: 0 } : { left: 0 }),
          background: "linear-gradient(#1d4d17, #2f7426)",
          translate: portrait ? `0 ${(split - 1) * height * 0.5}px` : `${(split - 1) * width * 0.5}px 0`,
          overflow: "hidden",
        }}
      >
        <Tiles name="grass_side" block={64} style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: 64, opacity: 0.9 }} />
        <BlockText text={copy.dual.noob} size={portrait ? 80 : 72} at={2} stagger={0.6} />
        <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
          {copy.dual.noobItems.map((item, i) => (
            <Rise key={i} at={noobAt(i)} distance={16}>
              <div style={{ fontFamily: F.pixel, fontSize: portrait ? 48 : 40, color: "#fff", textShadow: `4px 4px 0 ${C.shadow}`, display: "flex", gap: 20, alignItems: "center" }}>
                <span style={{ color: C.grass }}>✔</span>
                {item}
              </div>
            </Rise>
          ))}
        </div>
      </div>

      {/* The veteran's half: a terminal */}
      <div
        style={{
          ...box,
          ...(portrait ? { top: height / 2 } : { left: width / 2 }),
          background: C.terminal,
          opacity: 0.25 + 0.75 * proIn,
          backgroundImage: "repeating-linear-gradient(transparent 0 3px, rgba(255,255,255,.025) 3px 4px)",
        }}
      >
        <div style={{ fontFamily: F.mono, fontWeight: 700, fontSize: portrait ? 76 : 68, color: C.grass, opacity: proIn }}>{copy.dual.pro}</div>
        <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
          {copy.dual.proItems.map((item, i) => {
            const at = proAt(i);
            const n = Math.max(0, Math.floor((frame - at) * 2.2));
            return (
              <div key={i} style={{ fontFamily: F.mono, fontSize: portrait ? 42 : 36, color: "#d7e0e8", opacity: frame >= at ? 1 : 0, whiteSpace: "nowrap" }}>
                <span style={{ color: C.grass }}>&gt; </span>
                {item.slice(0, n)}
              </div>
            );
          })}
        </div>
      </div>

      {/* The block in the seam */}
      <Img
        src={tex("iso_grass")}
        style={{
          position: "absolute",
          left: width / 2 - 70,
          top: height / 2 - 70,
          width: 140,
          height: 140,
          imageRendering: "pixelated",
          scale: String(interpolate(frame, [4, 10], [0, 1], { ...clamp, easing: Easing.out(Easing.back(2)) })),
          rotate: `${interpolate(frame, [Math.round(half * B) - 3, Math.round(half * B) + 5], [0, 360], clamp)}deg`,
          filter: "drop-shadow(0 12px 18px rgba(0,0,0,.5))",
        }}
      />
      {copy.dual.noobItems.map((_, i) => <Sfx key={`n${i}`} at={noobAt(i)} name="pop" volume={0.7} />)}
      {copy.dual.proItems.map((_, i) => <Sfx key={`p${i}`} at={proAt(i)} name="key" volume={0.8} />)}
    </AbsoluteFill>
  );
};
