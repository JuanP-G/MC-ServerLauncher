import React from "react";
import { AbsoluteFill, random, useCurrentFrame, useVideoConfig } from "remotion";
import { tex } from "./World";

const KINDS = ["stone", "dirt", "grass_side", "stone", "gravel", "dirt"];

/**
 * The cut between features: blocks drop in to cover the screen, then are mined away from the
 * other side. `at` is the frame of the cut; it takes `half` frames each way.
 */
export const BlockWipe: React.FC<{ at: number; half?: number; block?: number; seed?: string }> = ({ at, half = 5, block = 120, seed = "w" }) => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  if (frame < at - half - 1 || frame > at + half + 1) return null;
  const cols = Math.ceil(width / block);
  const rows = Math.ceil(height / block);
  const cells = [];
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      // A diagonal sweep with a little noise, so it reads as blocks and not as a wipe.
      const d = (c / cols + r / rows) / 2 + (random(`${seed}${r}-${c}`) - 0.5) * 0.25;
      const inAt = at - half + d * half * 0.9;
      const outAt = at + d * half * 0.9;
      const shown = frame >= inAt && frame < outAt + 1;
      if (!shown) continue;
      const k = KINDS[Math.floor(random(`${seed}k${r}-${c}`) * KINDS.length)];
      const pop = Math.min(1, (frame - inAt + 1) / 2);
      cells.push(
        <div
          key={`${r}-${c}`}
          style={{
            position: "absolute",
            left: c * block,
            top: r * block,
            width: block,
            height: block,
            backgroundImage: `url(${tex(k)})`,
            backgroundSize: "100% 100%",
            imageRendering: "pixelated",
            scale: String(0.6 + 0.4 * pop),
            boxShadow: "inset 0 0 0 3px rgba(0,0,0,.25)",
          }}
        />,
      );
    }
  }
  return <AbsoluteFill style={{ zIndex: 50 }}>{cells}</AbsoluteFill>;
};

/** The screen breaking into blocks that fly off: the moment the click ends the chaos. */
export const Shatter: React.FC<{ color?: string; block?: number; length?: number }> = ({ color = "#000", block = 90, length = 14 }) => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  if (frame > length) return null;
  const cols = Math.ceil(width / block);
  const rows = Math.ceil(height / block);
  const cells = [];
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      const dx = c * block + block / 2 - width / 2;
      const dy = r * block + block / 2 - height / 2;
      const dist = Math.hypot(dx, dy) / Math.hypot(width / 2, height / 2);
      const delay = dist * 4 + random(`d${r}-${c}`) * 2;
      const t = Math.max(0, frame - delay);
      const v = 18 + random(`v${r}-${c}`) * 22;
      const x = (dx / (Math.hypot(dx, dy) + 1)) * v * t;
      const y = (dy / (Math.hypot(dx, dy) + 1)) * v * t + 2.2 * t * t;
      cells.push(
        <div
          key={`${r}-${c}`}
          style={{
            position: "absolute",
            left: c * block,
            top: r * block,
            width: block + 1,
            height: block + 1,
            background: color,
            translate: `${x}px ${y}px`,
            rotate: `${(random(`r${r}-${c}`) - 0.5) * t * 18}deg`,
            scale: String(Math.max(0, 1 - t / (length - delay + 1))),
            boxShadow: t > 0 ? "0 0 0 2px rgba(124,207,58,.35)" : undefined,
          }}
        />,
      );
    }
  }
  return <AbsoluteFill style={{ zIndex: 60 }}>{cells}</AbsoluteFill>;
};
