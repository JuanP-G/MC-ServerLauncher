import React from "react";
import { AbsoluteFill, random, useCurrentFrame, useVideoConfig } from "remotion";
import { Sfx } from "../components/Sfx";
import { World } from "../components/World";
import type { Copy } from "../i18n/types";
import { C, F } from "../theme";
import { useBeat } from "../timing";

/** The rest of the list, falling like blocks and piling up on the grass, two a beat. */
export const Wall: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const ground = height - 102;
  const perRow = portrait ? 2 : 3;
  const cellW = (width - 160) / perRow;
  const rowH = portrait ? 130 : 140;
  const step = (beats * B) / (copy.wall.length + 1);
  return (
    <AbsoluteFill>
      <World />
      {copy.wall.map((label, i) => {
        const at = Math.round(i * step);
        const t = frame - at;
        if (t < 0) return null;
        const row = Math.floor(i / perRow);
        const col = i % perRow;
        const restY = ground - (row + 1) * rowH - 8;
        const y = Math.min(restY, -140 + 0.5 * 9 * t * t + 20 * t);
        const landed = y >= restY;
        return (
          <div
            key={label}
            style={{
              position: "absolute",
              left: 80 + col * cellW + 8,
              top: y,
              width: cellW - 16,
              height: rowH - 14,
              display: "grid",
              placeItems: "center",
              background: C.inventory,
              boxShadow: "inset 5px 5px 0 #fff, inset -5px -5px 0 #555, 0 6px 0 rgba(0,0,0,.35)",
              fontFamily: F.pixel,
              fontSize: portrait ? 32 : 40,
              color: "#3f3f3f",
              rotate: landed ? "0deg" : `${(random(`w${i}`) - 0.5) * 10}deg`,
            }}
          >
            {label}
          </div>
        );
      })}
      {copy.wall.map((_, i) => <Sfx key={i} at={Math.round(i * step) + 8} name="block" volume={0.6} />)}
    </AbsoluteFill>
  );
};
