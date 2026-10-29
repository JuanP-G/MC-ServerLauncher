import { Audio } from "@remotion/media";
import React from "react";
import { AbsoluteFill, Easing, interpolate, Sequence, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { Footage, type Cam } from "../components/Footage";
import { LANGUAGES } from "../i18n";
import { F } from "../theme";
import { EndCard } from "./EndCard";
import { MANIFESTO, musicFile, songOf } from "./timeline";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

export type ManifestoProps = { edit: "b45" | "b30"; lang: string; music: boolean };

/** What each line is shown over: a stretch of a recording, and a slow camera move close to it. */
const SHOTS: { scene: string; range: [number, number]; cams: Cam[] }[] = [
  { scene: "hero", range: [0, 0], cams: [{ at: 0, x: 560, y: 260, zoom: 1.5 }, { at: 1, x: 860, y: 230, zoom: 1.8 }] },
  { scene: "console", range: [0, 3200], cams: [{ at: 0, x: 640, y: 560, zoom: 2.0 }, { at: 1, x: 760, y: 610, zoom: 2.3 }] },
  { scene: "internet", range: [900, 3400], cams: [{ at: 0, x: 760, y: 268, zoom: 2.1 }, { at: 1, x: 900, y: 266, zoom: 2.3 }] },
  { scene: "players", range: [1300, 4300], cams: [{ at: 0, x: 640, y: 470, zoom: 1.7 }, { at: 1, x: 720, y: 460, zoom: 1.9 }] },
  { scene: "create", range: [11200, 15500], cams: [{ at: 0, x: 640, y: 190, zoom: 1.7 }, { at: 1, x: 780, y: 170, zoom: 1.95 }] },
];

/** A line where *words between asterisks* are bold, each word fading up in turn. */
const Line: React.FC<{ text: string; size: number; align: "left" | "center"; start: number; leave: number }> = ({ text, size, align, start, leave }) => {
  const frame = useCurrentFrame();
  const words: { w: string; bold: boolean }[] = [];
  text.split("*").forEach((chunk, i) => chunk.split(" ").filter(Boolean).forEach((w) => words.push({ w, bold: i % 2 === 1 })));
  const out = interpolate(frame, [leave, leave + 14], [1, 0], clamp);
  return (
    <div style={{ display: "flex", flexWrap: "wrap", columnGap: size * 0.26, justifyContent: align === "center" ? "center" : "flex-start", opacity: out }}>
      {words.map(({ w, bold }, i) => {
        const at = start + 4 + i * 3;
        const p = interpolate(frame, [at, at + 16], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });
        return (
          <span
            key={i}
            style={{
              fontFamily: F.display,
              fontWeight: bold ? 800 : 300,
              fontStretch: "112%",
              fontSize: size,
              lineHeight: 1.12,
              letterSpacing: "-0.01em",
              color: "#fff",
              opacity: p,
              translate: `0 ${(1 - p) * size * 0.35}px`,
              filter: `blur(${(1 - p) * 6}px)`,
              textShadow: "0 4px 30px rgba(0,0,0,.55)",
            }}
          >
            {w}
          </span>
        );
      })}
    </div>
  );
};

/** One shot: the recording blurred and dark, with a sharp, lit window of focus where the camera looks. */
const Shot: React.FC<{ index: number; lang: string; duration: number; text: string; fade: number }> = ({ index, lang, duration, text, fade }) => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const shot = SHOTS[index];
  const cams = shot.cams.map((c) => ({ ...c, zoom: portrait ? Math.max(c.zoom * 1.6, 3.2) : c.zoom }));
  const range: [number, number] = shot.range[1] > 0 ? shot.range : [0, 1];
  const freeze = shot.range[1] > 0 ? undefined : 0;
  const opacity = interpolate(frame, [0, fade, duration - fade, duration], [0, 1, 1, 0], clamp);
  const focusX = interpolate(frame, [0, duration], [46, 54]);
  const footage = (
    <Footage scene={shot.scene} lang={lang} width={width} height={height} range={range} duration={duration} cams={cams} cursor={false} freezeAt={freeze} />
  );
  const mask = `radial-gradient(ellipse ${portrait ? "62% 18%" : "34% 26%"} at ${focusX}% 50%, #000 35%, transparent 100%)`;
  return (
    <AbsoluteFill style={{ opacity }}>
      <AbsoluteFill style={{ filter: "blur(16px) brightness(.42) saturate(.75)", scale: "1.06" }}>{footage}</AbsoluteFill>
      <AbsoluteFill style={{ WebkitMaskImage: mask, maskImage: mask, filter: "brightness(1.02) contrast(1.05)" }}>{footage}</AbsoluteFill>
      <AbsoluteFill
        style={{
          display: "flex",
          alignItems: portrait ? "center" : "flex-end",
          justifyContent: portrait ? "flex-end" : "flex-end",
          flexDirection: "column",
          padding: portrait ? "0 70px 300px" : "0 150px 190px",
        }}
      >
        <div style={{ width: "100%", maxWidth: portrait ? 940 : 1560 }}>
          {/* In after the crossfade has brought this shot up, out before the next one comes: never two at once. */}
          <Line text={text} size={portrait ? 84 : 104} align={portrait ? "center" : "left"} start={index === 0 ? 0 : fade} leave={duration - fade - 16} />
        </div>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};

/** Warm light drifting across the frame, grain, a vignette and, when wide, the bars of cinema. */
const Film: React.FC = () => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  const portrait = height > width;
  const bar = portrait ? 0 : Math.round((height - width / 2.39) / 2);
  const drift = Math.sin(frame / 90);
  return (
    <AbsoluteFill style={{ pointerEvents: "none" }}>
      <AbsoluteFill
        style={{
          background: `radial-gradient(circle at ${88 + drift * 6}% ${10 + drift * 4}%, rgba(242,182,96,.38), transparent 42%), radial-gradient(circle at ${8 - drift * 4}% 96%, rgba(124,207,58,.16), transparent 45%)`,
          mixBlendMode: "screen",
        }}
      />
      <AbsoluteFill style={{ background: "radial-gradient(ellipse at 50% 50%, transparent 55%, rgba(0,0,0,.7) 100%)" }} />
      <svg width={width} height={height} style={{ position: "absolute", inset: 0, opacity: 0.09, mixBlendMode: "overlay" }}>
        <filter id="grain">
          <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" seed={frame % 24} />
        </filter>
        <rect width="100%" height="100%" filter="url(#grain)" />
      </svg>
      {bar > 0 && (
        <>
          <div style={{ position: "absolute", left: 0, right: 0, top: 0, height: bar, background: "#000" }} />
          <div style={{ position: "absolute", left: 0, right: 0, bottom: 0, height: bar, background: "#000" }} />
        </>
      )}
    </AbsoluteFill>
  );
};

/** «Manifiesto»: slow, close shots of the app and a few big lines over Sweden. */
export const Manifesto: React.FC<ManifestoProps> = ({ edit, lang, music }) => {
  const { fps } = useVideoConfig();
  const tl = MANIFESTO[edit];
  const copy = LANGUAGES[lang];
  const fade = Math.round(1.1 * fps);
  const credit = `${copy.brand.music}: C418 — ${songOf(edit)} · Music courtesy of Mojang Studios`;
  return (
    <AbsoluteFill style={{ background: "#030405" }}>
      {tl.shots.map((s, i) => {
        const end = i + 1 < tl.shots.length ? tl.shots[i + 1].at : tl.end;
        const from = Math.max(0, Math.round(s.at * fps) - (i === 0 ? 0 : fade));
        const duration = Math.round(end * fps) + fade - from;
        return (
          <Sequence key={i} from={from} durationInFrames={duration} name={`shot ${i + 1}`}>
            <Shot index={s.line} lang={lang} duration={duration} text={copy.brand.manifesto[s.line]} fade={i === 0 ? Math.round(0.8 * fps) : fade} />
          </Sequence>
        );
      })}
      <Sequence from={Math.round(tl.end * fps)} name="end card">
        <EndCard copy={copy} credit={credit} pixel={false} dim={false} />
      </Sequence>
      <Film />
      {music && <Audio src={staticFile(musicFile(edit))} />}
    </AbsoluteFill>
  );
};

export const manifestoDuration = (edit: "b45" | "b30", fps: number) => Math.ceil(MANIFESTO[edit].total * fps);
