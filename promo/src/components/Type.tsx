import React from "react";
import { Easing, interpolate, useCurrentFrame } from "remotion";
import { C, F } from "../theme";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/**
 * A line in the game's letters where every letter drops in like a placed block, one after
 * another, with the hard shadow the game draws under its text.
 */
export const BlockText: React.FC<{
  text: string;
  size: number;
  at?: number;
  stagger?: number;
  color?: string;
  accent?: string[];
  accentColor?: string;
  align?: "left" | "center";
  style?: React.CSSProperties;
  out?: number;
}> = ({ text, size, at = 0, stagger = 1, color = C.ink, accent = [], accentColor = C.grass, align = "left", style, out }) => {
  const frame = useCurrentFrame();
  const shadow = Math.max(4, Math.round(size / 16) * 2);
  const words = text.split(" ");
  let i = 0;
  const leave = out === undefined ? 0 : interpolate(frame, [out, out + 6], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  return (
    <div
      style={{
        fontFamily: F.pixel,
        fontSize: size,
        lineHeight: 1.18,
        color,
        textShadow: `${shadow}px ${shadow}px 0 ${C.shadow}`,
        display: "flex",
        flexWrap: "wrap",
        justifyContent: align === "center" ? "center" : "flex-start",
        columnGap: size * 0.5,
        opacity: 1 - leave,
        translate: `0 ${-leave * size * 0.6}px`,
        ...style,
      }}
    >
      {words.map((w, wi) => {
        const isAccent = accent.some((a) => w.toLowerCase().includes(a.toLowerCase()));
        return (
          <span key={wi} style={{ whiteSpace: "nowrap", color: isAccent ? accentColor : undefined }}>
            {[...w].map((ch, ci) => {
              const t = at + (i++) * stagger;
              const p = interpolate(frame, [t, t + 7], [0, 1], { ...clamp, easing: Easing.bezier(0.2, 1.4, 0.4, 1) });
              return (
                <span
                  key={ci}
                  style={{ display: "inline-block", opacity: frame < t ? 0 : 1, translate: `0 ${(1 - p) * -size * 0.9}px` }}
                >
                  {ch}
                </span>
              );
            })}
          </span>
        );
      })}
    </div>
  );
};

/** Plain words that slide up into place: for the smaller line under a title. */
export const Rise: React.FC<{ at: number; children: React.ReactNode; style?: React.CSSProperties; distance?: number }> = ({
  at,
  children,
  style,
  distance = 30,
}) => {
  const frame = useCurrentFrame();
  const p = interpolate(frame, [at, at + 10], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });
  return <div style={{ opacity: p, translate: `0 ${(1 - p) * distance}px`, ...style }}>{children}</div>;
};

/** Text typed one character at a time, with a block cursor. */
export const Typed: React.FC<{ text: string; at: number; cps: number; fps: number; style?: React.CSSProperties; cursor?: boolean }> = ({
  text,
  at,
  cps,
  fps,
  style,
  cursor = true,
}) => {
  const frame = useCurrentFrame();
  const n = Math.max(0, Math.floor(((frame - at) / fps) * cps));
  const shown = text.slice(0, n);
  const blink = Math.floor(frame / (fps / 4)) % 2 === 0 || n < text.length;
  return (
    <div style={style}>
      {shown}
      {cursor && (
        <span style={{ display: "inline-block", width: "0.55em", height: "1em", verticalAlign: "-0.12em", marginLeft: 4, background: blink ? C.grass : "transparent" }} />
      )}
    </div>
  );
};
