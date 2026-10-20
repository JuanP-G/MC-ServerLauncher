import React from "react";
import { AbsoluteFill, Img, random, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { C } from "../theme";

export const tex = (name: string) => staticFile(`mc/${name}.png`);

/** A tiled Minecraft texture at a whole-number scale, so the pixels stay square and sharp. */
export const Tiles: React.FC<{ name: string; block: number; style?: React.CSSProperties }> = ({ name, block, style }) => (
  <div
    style={{
      backgroundImage: `url(${tex(name)})`,
      backgroundSize: `${block}px ${block}px`,
      imageRendering: "pixelated",
      ...style,
    }}
  />
);

/** The night landscape of the website: sky, stars, moon, drifting clouds and a strip of ground. */
export const World: React.FC<{ ground?: number; drift?: number; groundVisible?: number }> = ({ ground = 1.6, drift = 1, groundVisible = 1 }) => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  const block = 64;
  const groundH = block * ground * groundVisible;
  const stars = Array.from({ length: 70 }, (_, i) => ({
    x: random(`sx${i}`) * width,
    y: random(`sy${i}`) * height * 0.62,
    s: random(`ss${i}`) < 0.2 ? 6 : 4,
    tw: random(`st${i}`) * Math.PI * 2,
  }));
  const clouds = [0, 1, 2, 3].map((i) => ({
    y: 24 + 56 * i + random(`cy${i}`) * 20,
    w: 220 + random(`cw${i}`) * 260,
    speed: (0.6 + random(`cv${i}`) * 0.9) * drift,
    x0: random(`cx${i}`) * width,
  }));
  return (
    <AbsoluteFill style={{ background: `linear-gradient(${C.skyTop} 0%, ${C.skyMid} 62%, ${C.skyLow} 100%)` }}>
      {stars.map((s, i) => (
        <div
          key={i}
          style={{
            position: "absolute",
            left: Math.round(s.x / 4) * 4,
            top: Math.round(s.y / 4) * 4,
            width: s.s,
            height: s.s,
            background: "#fff",
            opacity: 0.35 + 0.45 * (0.5 + 0.5 * Math.sin(frame / 9 + s.tw)),
          }}
        />
      ))}
      <Img
        src={tex("moon")}
        style={{ position: "absolute", right: width * 0.08, top: height * 0.02, width: 192, height: 192, imageRendering: "pixelated", mixBlendMode: "screen" }}
      />
      {clouds.map((c, i) => {
        const x = ((c.x0 + frame * c.speed) % (width + c.w)) - c.w;
        return (
          <div key={i} style={{ position: "absolute", left: Math.round(x / 8) * 8, top: Math.round(c.y / 8) * 8, opacity: 0.9 }}>
            <div style={{ width: c.w, height: 32, background: "#f2f5f8", boxShadow: "0 16px 0 #c9d3dd" }} />
            <div style={{ position: "absolute", left: c.w * 0.2, top: -24, width: c.w * 0.45, height: 24, background: "#f2f5f8" }} />
          </div>
        );
      })}
      {groundH > 0 && (
        <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: groundH, overflow: "hidden" }}>
          <Tiles name="grass_side" block={block} style={{ position: "absolute", left: 0, right: 0, top: 0, height: block }} />
          <Tiles name="dirt" block={block} style={{ position: "absolute", left: 0, right: 0, top: block, bottom: 0 }} />
        </div>
      )}
    </AbsoluteFill>
  );
};
