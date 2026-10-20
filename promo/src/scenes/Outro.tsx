import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { GameButton } from "../components/Minecraft";
import { Sfx } from "../components/Sfx";
import { BlockText, Rise } from "../components/Type";
import { World } from "../components/World";
import type { Copy } from "../i18n/types";
import { C, F } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** The end card: the logo, what it costs (nothing), where it runs and where to get it. */
export const Outro: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const buttonAt = Math.round(B * Math.min(beats, 6));
  const drop = interpolate(frame, [0, 7], [-500, 0], { ...clamp, easing: Easing.bezier(0.3, 1.5, 0.5, 1) });
  const pop = interpolate(frame, [buttonAt, buttonAt + 7], [0, 1], { ...clamp, easing: Easing.out(Easing.back(2.4)) });
  const logo = portrait ? 260 : 200;
  return (
    <AbsoluteFill>
      <World drift={0.5} />
      <AbsoluteFill style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: portrait ? 34 : 22, paddingBottom: portrait ? 200 : 150 }}>
        <Img src={staticFile("brand/logo.png")} style={{ width: logo, height: logo, translate: `0 ${drop}px`, filter: "drop-shadow(0 18px 30px rgba(0,0,0,.45))" }} />
        <BlockText text="MC Server Launcher" size={portrait ? 64 : 96} at={3} stagger={0.5} align="center" accent={["MC"]} />
        <Rise at={Math.round(B * 1.5)}>
          <div style={{ fontFamily: F.display, fontWeight: 700, fontSize: portrait ? 42 : 38, color: "#eaf2f8", textAlign: "center", padding: "0 60px", textShadow: "0 3px 14px rgba(0,0,0,.45)" }}>
            {copy.outro.free}
          </div>
        </Rise>
        <Rise at={Math.round(B * 2.5)}>
          <div style={{ fontFamily: F.pixel, fontSize: portrait ? 40 : 32, color: C.grass, textShadow: `4px 4px 0 ${C.shadow}` }}>{copy.outro.platforms}</div>
        </Rise>
        <div style={{ marginTop: portrait ? 36 : 22, scale: String(pop), opacity: pop > 0 ? 1 : 0 }}>
          <GameButton label={copy.outro.download} size={portrait ? 50 : 40} icon />
        </div>
        <Rise at={buttonAt + 4}>
          <div style={{ fontFamily: F.mono, fontWeight: 700, fontSize: portrait ? 36 : 30, color: "#fff", textShadow: "0 2px 10px rgba(0,0,0,.5)" }}>{copy.outro.url}</div>
        </Rise>
      </AbsoluteFill>
      <div
        style={{
          position: "absolute",
          left: 40,
          right: 40,
          bottom: 22,
          textAlign: "center",
          fontFamily: F.display,
          fontWeight: 500,
          fontSize: portrait ? 22 : 18,
          color: "rgba(255,255,255,.85)",
          textShadow: "0 1px 4px rgba(0,0,0,.8)",
          opacity: interpolate(frame, [B * 2, B * 3], [0, 1], clamp),
        }}
      >
        {copy.outro.disclaimer}
      </div>
      <Sfx at={6} name="block" />
      <Sfx at={buttonAt} name="pop" />
    </AbsoluteFill>
  );
};
