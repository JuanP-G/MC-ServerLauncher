import React from "react";
import { Img, staticFile } from "remotion";
import { C, F } from "../theme";
import { tex } from "./World";

/**
 * A row of the game's multiplayer list: the server icon, its name, the message of the day and the
 * player count with signal bars. Sizes in multiples of 8 so the letters stay sharp.
 */
export const ServerEntry: React.FC<{
  name: string;
  motd: string;
  motdColor?: string;
  players: string;
  bars?: number;
  scale?: number;
  style?: React.CSSProperties;
}> = ({ name, motd, motdColor = "#aaaaaa", players, bars = 5, scale = 1, style }) => {
  const u = 8 * scale;
  return (
    <div
      style={{
        display: "flex",
        gap: u * 1.5,
        padding: u,
        width: u * 76,
        background: "rgba(0,0,0,.55)",
        border: `${scale * 2}px solid #808080`,
        fontFamily: F.pixel,
        fontSize: u * 2,
        lineHeight: 1.25,
        ...style,
      }}
    >
      <Img src={staticFile("brand/logo.png")} style={{ width: u * 8, height: u * 8, imageRendering: "pixelated", flexShrink: 0 }} />
      <div style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column", gap: u * 0.5 }}>
        <div style={{ display: "flex", color: "#fff" }}>
          <span>{name}</span>
          <span style={{ marginLeft: "auto", color: "#aaaaaa", display: "flex", alignItems: "flex-end", gap: u * 0.6 }}>
            {players}
            <span style={{ display: "inline-flex", alignItems: "flex-end", gap: scale * 2, height: u * 1.8 }}>
              {[1, 2, 3, 4, 5].map((b) => (
                <span key={b} style={{ width: scale * 3, height: (u * 1.8 * b) / 5, background: b <= bars ? "#3fdc3f" : "#444" }} />
              ))}
            </span>
          </span>
        </div>
        <div style={{ color: motdColor, whiteSpace: "nowrap", overflow: "hidden" }}>{motd}</div>
      </div>
    </div>
  );
};

/** An inventory slot holding an item icon, as on the website. */
export const ItemSlot: React.FC<{ item: string; size: number; style?: React.CSSProperties }> = ({ item, size, style }) => (
  <div
    style={{
      width: size,
      height: size,
      background: "#8b8b8b",
      boxShadow: `inset ${size / 18}px ${size / 18}px 0 #373737, inset ${-size / 18}px ${-size / 18}px 0 #ffffff`,
      display: "grid",
      placeItems: "center",
      ...style,
    }}
  >
    {/* The sun and moon are drawn additively in the game: their black has to vanish. */}
    <Img src={tex(item)} style={{ width: size * 0.74, height: size * 0.74, imageRendering: "pixelated", mixBlendMode: item === "moon" || item === "sun" ? "screen" : undefined, scale: item === "moon" ? "1.8" : undefined }} />
  </div>
);

/** The green button of the game's newer menus, flat with a bevel. */
export const GameButton: React.FC<{ label: string; size: number; pressed?: boolean; icon?: boolean; style?: React.CSSProperties }> = ({
  label,
  size,
  pressed = false,
  icon = false,
  style,
}) => (
  <div
    style={{
      display: "inline-flex",
      alignItems: "center",
      gap: size * 0.45,
      padding: `${size * 0.55}px ${size * 1.1}px ${size * 0.55 + (pressed ? 0 : size * 0.18)}px`,
      background: pressed ? C.grassDark : "#3c8527",
      boxShadow: pressed
        ? `inset 0 ${size * 0.12}px 0 rgba(0,0,0,.35), 0 0 0 ${size * 0.08}px #1e1e1f`
        : `inset 0 ${-size * 0.18}px 0 #1d4d13, inset 0 ${size * 0.08}px 0 #52a535, 0 0 0 ${size * 0.08}px #1e1e1f`,
      translate: `0 ${pressed ? size * 0.12 : 0}px`,
      color: "#fff",
      fontFamily: F.display,
      fontWeight: 700,
      fontSize: size,
      whiteSpace: "nowrap",
      ...style,
    }}
  >
    {icon && (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
        <path d="M12 3v12M6.5 10.5 12 16l5.5-5.5M4 20h16" />
      </svg>
    )}
    {label}
  </div>
);
