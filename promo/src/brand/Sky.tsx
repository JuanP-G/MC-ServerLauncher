import React from "react";
import { AbsoluteFill, Img, random, useVideoConfig } from "remotion";
import { tex } from "../components/World";

type RGB = [number, number, number];
const hex = (h: string): RGB => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)) as RGB;
const mix = (a: RGB, b: RGB, k: number) => a.map((v, i) => Math.round(v + (b[i] - v) * k)) as RGB;
const css = (c: RGB) => `rgb(${c[0]},${c[1]},${c[2]})`;

const NIGHT = [hex("#0a1830"), hex("#1d4573"), hex("#2a6398")];
const DAY = [hex("#4f8fd6"), hex("#86bdeb"), hex("#b9dcf5")];
const DAWN = [hex("#24365e"), hex("#b86c5a"), hex("#f0a868")];

/**
 * The sky over the village: `day` 0 is night and 1 is noon, `warm` pulls it toward dawn, and
 * `arc` (0..1) is where the sun is on its way across, the moon opposite it.
 */
export const Sky: React.FC<{ day: number; warm: number; arc: number }> = ({ day, warm, arc }) => {
  const { width, height } = useVideoConfig();
  const stops = [0, 1, 2].map((i) => mix(mix(NIGHT[i], DAY[i], day), DAWN[i], warm));
  const stars = Array.from({ length: 80 }, (_, i) => ({ x: random(`bx${i}`) * width, y: random(`by${i}`) * height * 0.6, s: random(`bs${i}`) < 0.2 ? 6 : 4 }));
  // Low enough that neither passes behind the captions at the top.
  const size = 288;
  const body = (k: number) => ({
    left: width * (0.08 + 0.84 * k) - size / 2,
    top: height * (0.66 - 0.36 * Math.sin(Math.PI * Math.min(1, Math.max(0, k)))) - size / 2,
  });
  const sun = body(arc);
  const moon = body((arc + 0.5) % 1);
  return (
    <AbsoluteFill style={{ background: `linear-gradient(${css(stops[0])} 0%, ${css(stops[1])} 62%, ${css(stops[2])} 100%)` }}>
      {stars.map((s, i) => (
        <div key={i} style={{ position: "absolute", left: Math.round(s.x / 4) * 4, top: Math.round(s.y / 4) * 4, width: s.s, height: s.s, background: "#fff", opacity: 0.75 * (1 - day) * (1 - warm * 0.6) }} />
      ))}
      <Img src={tex("sun")} style={{ position: "absolute", ...sun, width: size, height: size, imageRendering: "pixelated", mixBlendMode: "screen", opacity: day }} />
      <Img src={tex("moon")} style={{ position: "absolute", ...moon, width: size, height: size, imageRendering: "pixelated", mixBlendMode: "screen", opacity: 1 - day }} />
    </AbsoluteFill>
  );
};
