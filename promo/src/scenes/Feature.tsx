import React from "react";
import { AbsoluteFill, Easing, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { AppWindow } from "../components/AppWindow";
import { Footage } from "../components/Footage";
import { ItemSlot, ServerEntry } from "../components/Minecraft";
import { BlockText, Rise } from "../components/Type";
import { World } from "../components/World";
import type { Copy } from "../i18n/types";
import { featureLayout, SHOTS, type FeatureKey } from "../shots";
import { C, F } from "../theme";
import { useBeat } from "../timing";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

/** One feature: its name in the game's letters and the real app doing it, in a floating window. */
export const Feature: React.FC<{ copy: Copy; lang: string; feature: FeatureKey; beats: number; index: number }> = ({
  copy,
  lang,
  feature,
  beats,
  index,
}) => {
  const frame = useCurrentFrame();
  const B = useBeat();
  const { width, height } = useVideoConfig();
  const duration = Math.round(beats * B);
  const shot = SHOTS[feature];
  const take = beats >= 8 ? shot.long : shot.short;
  const L = featureLayout(width, height, index);
  const text = copy.features[feature];
  const float = Math.sin(frame / 18 + index) * 6;
  const tilt = L.tilt + interpolate(frame, [0, duration], [0, -L.tilt * 0.45]);
  const titleSize = L.portrait ? 72 : 56;
  const enter = interpolate(frame, [0, 9], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });

  // The window: the footage, scaled up a little more in portrait where the screen is narrow.
  const zoomBoost = L.portrait ? 1.35 : 1;
  const cams = take.cams?.map((c) => ({ ...c, zoom: c.zoom * zoomBoost }));
  const recordingMs = take.range[0] + (take.range[1] - take.range[0]) * Math.min(1, frame / Math.max(1, duration - 1));

  return (
    <AbsoluteFill>
      <World />
      <div style={{ position: "absolute", inset: 0, perspective: 1800 }}>
        <div
          style={{
            position: "absolute",
            left: L.win.x,
            top: L.win.y + float + (1 - enter) * 60,
            width: L.win.w,
            transformStyle: "preserve-3d",
            rotate: `0 1 0 ${tilt}deg`,
            opacity: enter,
          }}
        >
          <AppWindow width={L.win.w} height={L.win.h}>
            <Footage
              scene={shot.scene}
              lang={lang}
              width={L.win.w}
              height={L.win.h}
              range={take.range}
              duration={duration}
              cams={cams}
              zoom={(take.zoom ?? 1) * zoomBoost}
            />
          </AppWindow>
        </div>
      </div>

      {/* The words */}
      <div
        style={{
          position: "absolute",
          left: L.text.x,
          top: L.portrait ? L.text.y : 0,
          bottom: L.portrait ? undefined : 102,
          width: L.text.w,
          display: "flex",
          flexDirection: "column",
          justifyContent: L.portrait ? "flex-start" : "center",
          alignItems: L.portrait ? "center" : "flex-start",
          gap: 28,
        }}
      >
        <div style={{ scale: String(interpolate(frame, [0, 6], [0.4, 1], { ...clamp, easing: Easing.out(Easing.back(2.2)) })) }}>
          <ItemSlot item={shot.item} size={L.portrait ? 104 : 96} />
        </div>
        <BlockText text={text.title} size={titleSize} at={2} stagger={0.6} align={L.portrait ? "center" : "left"} style={{ textAlign: L.portrait ? "center" : "left" }} />
        <Rise at={Math.round(B * 1.5)}>
          <div
            style={{
              fontFamily: F.display,
              fontWeight: 500,
              fontSize: L.portrait ? 38 : 32,
              lineHeight: 1.35,
              color: "#dce7f0",
              textAlign: L.portrait ? "center" : "left",
              maxWidth: L.portrait ? 900 : 500,
              textShadow: "0 2px 12px rgba(0,0,0,.5)",
            }}
          >
            {text.sub}
          </div>
        </Rise>
        {feature === "sleep" && <SleepEntry copy={copy} ms={recordingMs} portrait={L.portrait} />}
      </div>
    </AbsoluteFill>
  );
};

/** The game's server list, the other side of sleeping: what a player sees while the app naps. */
const SleepEntry: React.FC<{ copy: Copy; ms: number; portrait: boolean }> = ({ copy, ms, portrait }) => {
  const asleep = ms >= 2000 && ms < 3400;
  const waking = ms >= 3400 && ms < 4300;
  const motd = asleep ? copy.sleep.motdSleeping : waking ? copy.sleep.motdStarting : copy.community.motd;
  const players = asleep || waking ? "0/20" : ms >= 4300 ? "1/20" : "0/20";
  return (
    <Rise at={8} distance={20}>
      <ServerEntry
        name={copy.sleep.serverName}
        motd={motd}
        motdColor={asleep ? "#aaaaaa" : waking ? C.warn : "#55ff55"}
        players={players}
        bars={asleep ? 0 : 5}
        scale={portrait ? 1 : 0.875}
      />
    </Rise>
  );
};
