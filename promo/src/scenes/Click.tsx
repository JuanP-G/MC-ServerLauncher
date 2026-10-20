import React from "react";
import { AbsoluteFill, Easing, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { Cursor } from "../components/Footage";
import { GameButton } from "../components/Minecraft";
import { Sfx } from "../components/Sfx";
import { BlockText } from "../components/Type";
import type { Copy } from "../i18n/types";
import { C } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** Silence, a cursor, one click on the game's green button. */
export const Click: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const clickAt = Math.round((beats >= 4 ? 2 : 1) * B);
  const pressed = frame >= clickAt && frame < clickAt + 4;
  const travel = interpolate(frame, [2, clickAt - 1], [0, 1], { ...clamp, easing: Easing.inOut(Easing.cubic) });
  const btnSize = portrait ? 56 : 52;
  const cx = width / 2 + btnSize * 2.2;
  const cy = height / 2 + (portrait ? 140 : 120) + btnSize * 0.4;
  const from = { x: width * 0.86, y: height * 0.9 };
  const x = from.x + (cx - from.x) * travel;
  const y = from.y + (cy - from.y) * travel;
  const flash = interpolate(frame, [clickAt, clickAt + 1, clickAt + 4], [0, 0.9, 0], clamp);
  const size = portrait ? 80 : 96;
  return (
    <AbsoluteFill style={{ background: "#050505" }}>
      <AbsoluteFill style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: 40, paddingBottom: 120 }}>
        <div style={{ display: "flex", gap: size * 0.45, flexWrap: "wrap", justifyContent: "center", padding: "0 60px" }}>
          <BlockText text={copy.click.now} size={size} at={0} />
          {frame >= clickAt && <BlockText text={copy.click.oneClick} size={size} at={clickAt} stagger={0.5} accent={[copy.click.oneClick.split(" ")[1] ?? ""]} />}
        </div>
      </AbsoluteFill>
      <div style={{ position: "absolute", left: width / 2, top: height / 2 + (portrait ? 140 : 120), translate: "-50% 0" }}>
        <GameButton label={copy.click.button} size={btnSize} pressed={pressed} />
      </div>
      <div style={{ position: "absolute", left: 0, top: 0, scale: "2", transformOrigin: `${x}px ${y}px` }}>
        <Cursor x={x} y={y} size={26} />
      </div>
      <AbsoluteFill style={{ background: C.ink, opacity: flash }} />
      <Sfx at={clickAt} name="bigclick" />
    </AbsoluteFill>
  );
};
