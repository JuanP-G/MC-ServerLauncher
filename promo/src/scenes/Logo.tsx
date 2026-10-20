import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { AppWindow } from "../components/AppWindow";
import { Shatter } from "../components/BlockWipe";
import { Footage } from "../components/Footage";
import { Sfx } from "../components/Sfx";
import { BlockText, Rise } from "../components/Type";
import { World } from "../components/World";
import type { Copy } from "../i18n/types";
import { featureLayout } from "../shots";
import { F } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** The drop: the black breaks into blocks, the logo lands, the name stamps in, the app rises. */
export const Logo: React.FC<{ copy: Copy; lang: string; beats: number }> = ({ copy, lang, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const rises = beats >= 8;
  const riseAt = Math.round(B * 5.5);
  const lift = rises ? interpolate(frame, [riseAt, beats * B], [0, 1], { ...clamp, easing: Easing.inOut(Easing.cubic) }) : 0;
  const drop = interpolate(frame, [0, 7], [-600, 0], { ...clamp, easing: Easing.bezier(0.3, 1.5, 0.5, 1) });
  const logo = portrait ? 300 : 240;
  const L = featureLayout(width, height, 0);
  const winY = interpolate(lift, [0, 1], [height + 40, L.win.y]);
  return (
    <AbsoluteFill>
      <World />
      <AbsoluteFill
        style={{
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: "center",
          gap: 26,
          paddingBottom: 120,
          translate: `0 ${-lift * height * 0.75}px`,
          opacity: 1 - lift,
        }}
      >
        <Img src={staticFile("brand/logo.png")} style={{ width: logo, height: logo, translate: `0 ${drop}px`, filter: "drop-shadow(0 18px 30px rgba(0,0,0,.45))" }} />
        <BlockText text="MC Server Launcher" size={portrait ? 64 : 104} at={Math.round(B)} stagger={0.7} align="center" accent={["MC"]} />
        <Rise at={Math.round(B * 3)}>
          <div style={{ fontFamily: F.display, fontWeight: 700, fontSize: portrait ? 44 : 42, color: "#e8f1f8", textAlign: "center", padding: "0 60px", textShadow: "0 3px 14px rgba(0,0,0,.45)" }}>
            {copy.logo.tagline}
          </div>
        </Rise>
      </AbsoluteFill>
      {rises && (
        <div style={{ position: "absolute", inset: 0, perspective: 1800 }}>
          <div style={{ position: "absolute", left: L.win.x, top: winY, width: L.win.w, rotate: `0 1 0 ${L.tilt}deg` }}>
            <AppWindow width={L.win.w} height={L.win.h}>
              <Footage scene="hero" lang={lang} width={L.win.w} height={L.win.h} duration={1} freezeAt={0} cursor={false} cams={[{ at: 0, x: 760, y: 330, zoom: portrait ? 1.65 : 1 }]} />
            </AppWindow>
          </div>
        </div>
      )}
      <Shatter />
      <Sfx at={6} name="block" />
      {rises && <Sfx at={riseAt} name="whoosh" volume={0.7} />}
    </AbsoluteFill>
  );
};
