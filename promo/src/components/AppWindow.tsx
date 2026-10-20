import React from "react";
import { Img, staticFile } from "remotion";
import { F } from "../theme";

/** The window around the footage, with its title bar and — ▢ ✕, as on the website. */
export const AppWindow: React.FC<{ width: number; height: number; children: React.ReactNode; style?: React.CSSProperties; bar?: boolean }> = ({
  width,
  height,
  children,
  style,
  bar = true,
}) => {
  const barH = bar ? Math.round(Math.max(26, width * 0.022)) : 0;
  const icon = barH * 0.62;
  return (
    <div
      style={{
        position: "absolute",
        width,
        height: height + barH,
        borderRadius: 12,
        overflow: "hidden",
        background: "#000",
        boxShadow: "0 40px 90px rgba(0,0,0,.55), 0 0 0 1.5px rgba(255,255,255,.10)",
        ...style,
      }}
    >
      {bar && (
        <div
          style={{
            height: barH,
            background: "#1c1c1c",
            display: "flex",
            alignItems: "center",
            gap: barH * 0.35,
            paddingLeft: barH * 0.45,
            fontFamily: F.display,
            fontWeight: 700,
            fontSize: barH * 0.46,
            color: "#e6e6e6",
          }}
        >
          <Img src={staticFile("brand/logo.png")} style={{ width: icon, height: icon }} />
          MC Server Launcher
          <div style={{ marginLeft: "auto", display: "flex", height: "100%" }}>
            {["—", "▢", "✕"].map((g) => (
              <div key={g} style={{ width: barH * 1.7, display: "grid", placeItems: "center", color: "#cfcfcf", fontWeight: 500 }}>
                {g}
              </div>
            ))}
          </div>
        </div>
      )}
      <div style={{ position: "relative", width, height }}>{children}</div>
    </div>
  );
};
