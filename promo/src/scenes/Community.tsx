import React from "react";
import { AbsoluteFill, Easing, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { ServerEntry } from "../components/Minecraft";
import { Sfx } from "../components/Sfx";
import { BlockText } from "../components/Type";
import { World } from "../components/World";
import type { Copy } from "../i18n/types";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** From three friends to a full house: the player count runs up and the server list fills. */
export const Community: React.FC<{ copy: Copy; beats: number }> = ({ copy, beats }) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const switchAt = Math.round(B * (beats >= 8 ? 3.5 : 1.5));
  const big = frame >= switchAt;
  const count = Math.round(interpolate(frame, [switchAt, switchAt + B * 2], [3, 187], { ...clamp, easing: Easing.out(Easing.cubic) }));
  const scale = portrait ? 1.25 : 1.625;
  const others = copy.community.others;
  return (
    <AbsoluteFill>
      <World drift={2} />
      <AbsoluteFill style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: 48, paddingBottom: 110 }}>
        <div style={{ height: portrait ? 200 : 120, display: "grid", placeItems: "center", padding: "0 60px" }}>
          {!big ? (
            <BlockText key="f" text={copy.community.friends} size={portrait ? 80 : 96} at={1} stagger={0.6} align="center" out={switchAt - 6} />
          ) : (
            <BlockText key="c" text={copy.community.community} size={portrait ? 80 : 96} at={switchAt} stagger={0.5} align="center" accent={[copy.community.community.split(" ").slice(-1)[0].replace(".", "")]} />
          )}
        </div>
        <div style={{ display: "flex", flexDirection: "column", gap: 10 * scale }}>
          <ServerEntry
            name={copy.community.serverName}
            motd={copy.community.motd}
            motdColor="#55ff55"
            players={big ? `${count}/200` : "3/20"}
            scale={scale}
          />
          {others.map((o, i) => {
            const at = switchAt + 4 + i * 4;
            const p = interpolate(frame, [at, at + 6], [0, 1], { ...clamp, easing: Easing.out(Easing.back(1.6)) });
            return (
              <div key={o.name} style={{ opacity: p, translate: `0 ${(1 - p) * 40}px` }}>
                <ServerEntry name={o.name} motd={o.motd} motdColor="#ffff55" players={o.players} scale={scale} />
              </div>
            );
          })}
        </div>
      </AbsoluteFill>
      <Sfx at={switchAt} name="whoosh" volume={0.6} />
      <Sfx at={switchAt + Math.round(B * 2)} name="ding" volume={0.8} />
      {others.map((_, i) => <Sfx key={i} at={switchAt + 4 + i * 4} name="pop" volume={0.6} />)}
    </AbsoluteFill>
  );
};
