import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { BlockText, Rise } from "../components/Type";
import type { Copy } from "../i18n/types";
import { F } from "../theme";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/**
 * The signature both brand videos end on: the logo, the name, what it costs and where it runs,
 * the website, and in small print the disclaimer and the music credit. `pixel` sets the name in
 * the game's letters; without it, in the manifesto's type.
 */
export const EndCard: React.FC<{ copy: Copy; credit: string; pixel?: boolean; dim?: boolean }> = ({ copy, credit, pixel = true, dim = true }) => {
  const frame = useCurrentFrame();
  const { fps, width, height } = useVideoConfig();
  const portrait = height > width;
  const shade = interpolate(frame, [0, fps], [0, 1], clamp);
  const logo = interpolate(frame, [0, 0.8 * fps], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });
  return (
    <AbsoluteFill>
      {dim && <AbsoluteFill style={{ background: "radial-gradient(ellipse at 50% 42%, rgba(5,10,22,.55), rgba(5,10,22,.82))", opacity: shade }} />}
      <AbsoluteFill style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: portrait ? 30 : 22, paddingBottom: portrait ? 160 : 90 }}>
        <Img
          src={staticFile("brand/logo.png")}
          style={{ width: portrait ? 240 : 190, height: portrait ? 240 : 190, opacity: logo, scale: String(0.85 + 0.15 * logo), filter: "drop-shadow(0 16px 28px rgba(0,0,0,.5))" }}
        />
        {pixel ? (
          <BlockText text="MC Server Launcher" size={portrait ? 64 : 88} at={Math.round(0.4 * fps)} stagger={0.5} align="center" accent={["MC"]} />
        ) : (
          <Rise at={Math.round(0.4 * fps)}>
            <div style={{ fontFamily: F.display, fontWeight: 800, fontStretch: "112%", fontSize: portrait ? 78 : 120, color: "#fff", letterSpacing: "-0.01em" }}>MC Server Launcher</div>
          </Rise>
        )}
        <Rise at={Math.round(1.1 * fps)}>
          <div style={{ fontFamily: F.display, fontWeight: pixel ? 700 : 300, fontSize: portrait ? 40 : 36, color: "#e8f0f6", textAlign: "center", textShadow: "0 3px 14px rgba(0,0,0,.45)" }}>
            {copy.brand.tagline}
          </div>
        </Rise>
        <Rise at={Math.round(1.6 * fps)}>
          <div style={{ fontFamily: F.mono, fontWeight: 700, fontSize: portrait ? 34 : 28, color: "#fff", marginTop: 8 }}>{copy.outro.url}</div>
        </Rise>
      </AbsoluteFill>
      <div
        style={{
          position: "absolute",
          left: 40,
          right: 40,
          bottom: portrait ? 40 : 24,
          textAlign: "center",
          fontFamily: F.display,
          fontWeight: 500,
          fontSize: portrait ? 21 : 17,
          lineHeight: 1.5,
          color: "rgba(255,255,255,.82)",
          opacity: interpolate(frame, [fps, 2 * fps], [0, 1], clamp),
        }}
      >
        {credit}
        <br />
        {copy.outro.disclaimer}
      </div>
    </AbsoluteFill>
  );
};
