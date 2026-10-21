import { Audio } from "@remotion/media";
import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, Sequence, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { Footage } from "../components/Footage";
import { Sfx } from "../components/Sfx";
import { tex } from "../components/World";
import { LANGUAGES } from "../i18n";
import type { Copy } from "../i18n/types";
import { EndCard } from "../brand/EndCard";
import { Sky } from "../brand/Sky";
import { F } from "../theme";
import { Kitchen, popsAt, SevenSeg, STAGE, type KitchenState } from "./Kitchen";
import { SPOT, type SpotTimeline } from "./timeline";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const whip = { ...clamp, easing: Easing.bezier(0.7, 0, 0.2, 1) } as const;

export type SpotProps = { lang: string; music: boolean };

type Rect = { x: number; y: number; w: number; h: number };
const pad = (r: Rect, k: number): Rect => ({ x: r.x - (r.w * k) / 2, y: r.y - (r.h * k) / 2, w: r.w * (1 + k), h: r.h * (1 + k) });

/** The shots on the kitchen set: which part of it fills the frame, from when. Moves take `move` s. */
const shotsOf = (T: SpotTimeline, portrait: boolean): { at: number; rect: Rect; move: number }[] => {
  const wide: Rect = portrait ? { x: 120, y: 80, w: 1780, h: 900 } : { x: 60, y: 120, w: 1860, h: 900 };
  return [
    { at: 0, rect: pad(STAGE.lcd, portrait ? 1.8 : 2.4), move: 0 },
    { at: T.press - 0.2, rect: pad({ x: STAGE.lcd.x, y: STAGE.lcd.y, w: STAGE.lcd.w, h: STAGE.start.y + STAGE.start.h - STAGE.lcd.y }, 0.9), move: 0.3 },
    { at: T.press + 0.35, rect: wide, move: 0.45 },
    { at: T.phone, rect: pad(STAGE.phone, 0.25), move: 0.35 },
    { at: T.riff - 0.35, rect: STAGE.screen, move: 0.35 },
    { at: T.phone2, rect: pad(STAGE.phone, 0.25), move: 0.3 },
    { at: T.last, rect: pad(STAGE.lcd, 0.5), move: 0.35 },
    { at: T.ding + 0.05, rect: pad(STAGE.microwave, portrait ? 0.15 : 0.35), move: 0.25 },
  ];
};

const cameraAt = (t: number, shots: ReturnType<typeof shotsOf>, vw: number, vh: number) => {
  let i = 0;
  while (i + 1 < shots.length && shots[i + 1].at <= t) i++;
  const cur = shots[i];
  const prev = shots[Math.max(0, i - 1)];
  const k = cur.move > 0 ? interpolate(t, [cur.at, cur.at + cur.move], [0, 1], whip) : 1;
  const r = { x: prev.rect.x + (cur.rect.x - prev.rect.x) * k, y: prev.rect.y + (cur.rect.y - prev.rect.y) * k, w: prev.rect.w + (cur.rect.w - prev.rect.w) * k, h: prev.rect.h + (cur.rect.h - prev.rect.h) * k };
  const s = Math.min(vw / r.w, vh / r.h);
  // Blur while moving fast, the way a whip pan smears.
  const speed = cur.move > 0 && k > 0 && k < 1 ? Math.sin(Math.PI * k) : 0;
  return { s, tx: vw / 2 - (r.x + r.w / 2) * s, ty: vh / 2 - (r.y + r.h / 2) * s, blur: speed * 10 };
};

/** Seconds left on the microwave: a normal start, a blur while you work, then the last three slowly. */
const secondsLeft = (t: number, T: SpotTimeline) => {
  if (t < T.press) return null;
  if (t < T.last) return Math.round(interpolate(t, [T.press, T.riff, T.last], [120, 116, 3], { ...clamp, easing: Easing.in(Easing.quad) }));
  if (t >= T.ding) return 0;
  return Math.max(1, 3 - Math.floor((t - T.last - 0.2) / ((T.ding - T.last - 0.2) / 3)));
};
const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;

const Chat: React.FC<{ copy: Copy; t: number; T: SpotTimeline }> = ({ copy, t, T }) => {
  const msgs = [
    ...copy.spot.chat.map((m, i) => ({ ...m, at: T.phone + 0.25 + i * 0.55, link: false })),
    { text: "survival-friends.gl.joinmc.link", at: T.phone2 + 0.3, link: true },
    { who: "Alex", text: copy.spot.already, at: T.phone2 + 1.0, link: false },
  ].filter((m) => t >= m.at);
  return (
    <div style={{ position: "absolute", inset: 0, background: "#0b141a", display: "flex", flexDirection: "column", fontFamily: F.display }}>
      <div style={{ height: 34, background: "#1f2c34", color: "#e9edef", fontSize: 15, fontWeight: 700, display: "flex", alignItems: "center", paddingLeft: 10 }}>{copy.spot.group}</div>
      <div style={{ flex: 1, display: "flex", flexDirection: "column", justifyContent: "flex-end", gap: 6, padding: 8 }}>
        {msgs.slice(-5).map((m, i) => {
          const me = !("who" in m) || !m.who;
          const p = interpolate(t, [m.at, m.at + 0.15], [0.6, 1], clamp);
          return (
            <div key={i} style={{ alignSelf: me ? "flex-end" : "flex-start", maxWidth: "88%", background: me ? "#005c4b" : "#202c33", color: "#e9edef", borderRadius: 8, padding: "4px 7px", fontSize: m.link ? 11 : 14, fontFamily: m.link ? F.mono : F.display, lineHeight: 1.3, scale: String(p), transformOrigin: me ? "100% 100%" : "0% 100%", wordBreak: "break-all" }}>
              {!me && <div style={{ fontSize: 10, fontWeight: 700, color: "#53bdeb" }}>{m.who}</div>}
              {m.text}
            </div>
          );
        })}
      </div>
    </div>
  );
};

const HOUSE = ["    kkkk    ", "  kkkkkkkk  ", " kPPPPPPPPk ", " lPGPPdPGPl ", "tlPPPPDPPPlt"];
const TEX: Record<string, string> = { k: "dark_oak_planks", P: "oak_planks", G: "glass", d: "oak_door_top", D: "oak_door_bottom", l: "spruce_log", t: "torch" };

/** The last scene: inside the game, the friends turn up and the chat delivers the punchline. */
const World: React.FC<{ copy: Copy; T: SpotTimeline }> = ({ copy, T }) => {
  const frame = useCurrentFrame();
  const { fps, width, height } = useVideoConfig();
  const t = frame / fps;
  const portrait = height > width;
  const bp = portrait ? 130 : 130;
  const ground = height * (portrait ? 0.7 : 0.78);
  const people = [
    { skin: "steve", name: copy.spot.you, x: -0.5, at: 0 },
    { skin: "alex", name: "Alex", x: -3.2, at: 0.45 },
    { skin: "kai", name: "Kai", x: 2.2, at: 0.9 },
  ];
  const lines = [`Alex joined the game`, `Kai joined the game`, ...copy.spot.gameChat].map((l, i) => ({ l, at: [0.45, 0.9, 1.6, 2.5][i] }));
  const zoom = interpolate(t, [0, T.end - T.world], [1, 1.06]);
  return (
    <AbsoluteFill style={{ background: "#000" }}>
      <AbsoluteFill style={{ scale: String(zoom) }}>
        <Sky day={0} warm={0} arc={0.25} />
        <div style={{ position: "absolute", left: 0, right: 0, top: ground, bottom: 0, backgroundImage: `url(${tex("grass_side")}), url(${tex("dirt")})`, backgroundSize: `${bp}px ${bp}px`, backgroundRepeat: "repeat-x, repeat", backgroundPosition: `${width / 2}px 0, ${width / 2}px ${bp}px`, imageRendering: "pixelated" }} />
        {HOUSE.map((row, r) =>
          [...row].map((c, x) =>
            TEX[c] ? (
              <div key={`${r}-${x}`} style={{ position: "absolute", left: width / 2 + (x - 5.5) * bp * 0.8, top: ground - (HOUSE.length - r) * bp * 0.8, width: bp * 0.8 + 0.5, height: bp * 0.8 + 0.5, backgroundImage: `url(${tex(TEX[c])})`, backgroundSize: "100% 100%", imageRendering: "pixelated", filter: "brightness(.7)" }} />
            ) : null,
          ),
        )}
        {people.map((p) => {
          const dt = t - p.at;
          if (dt < 0) return null;
          const pop = interpolate(dt, [0, 0.25], [0, 1], { ...clamp, easing: Easing.out(Easing.back(2)) });
          return (
            <div key={p.name} style={{ position: "absolute", left: width / 2 + p.x * bp, top: ground - bp * 2, width: bp, height: bp * 2, scale: String(pop), transformOrigin: "50% 100%" }}>
              <Img src={staticFile(`mc/players/${p.skin}.png`)} style={{ width: bp, height: bp * 2, imageRendering: "pixelated" }} />
              <div style={{ position: "absolute", left: "50%", bottom: bp * 2.2, translate: "-50% 0", fontFamily: F.pixel, fontSize: 32, color: "#fff", background: "rgba(0,0,0,.42)", padding: "4px 10px", whiteSpace: "nowrap" }}>{p.name}</div>
            </div>
          );
        })}
      </AbsoluteFill>
      <div style={{ position: "absolute", left: portrait ? 40 : 48, bottom: portrait ? 180 : 40, display: "flex", flexDirection: "column", gap: 6, alignItems: "flex-start" }}>
        {lines.filter((x) => t >= x.at).map((x, i) => (
          <div key={i} style={{ fontFamily: F.pixel, fontSize: portrait ? 40 : 40, color: x.l.includes("joined") ? "#ffff55" : "#fff", background: "rgba(0,0,0,.4)", padding: "4px 12px", textShadow: "4px 4px 0 #3f3f3f" }}>{x.l}</div>
        ))}
      </div>
    </AbsoluteFill>
  );
};

/** «Antes de que pite»: two minutes on the microwave, a real server made in them. */
export const Spot: React.FC<SpotProps> = ({ lang, music }) => {
  const frame = useCurrentFrame();
  const { fps, width, height } = useVideoConfig();
  const portrait = height > width;
  const T = SPOT;
  const copy = LANGUAGES[lang];
  const t = frame / fps;
  const left = secondsLeft(t, T);
  const kitchen: KitchenState = {
    lcd: left === null ? (Math.floor(t * 2.5) % 2 ? "2:00" : "-:--") : mmss(left),
    on: left !== null && t < T.ding,
    run: Math.max(0, t - T.press),
    puff: interpolate(t, [T.press + 1, T.ding], [0, 1], clamp),
    pops: t < T.ding ? popsAt(t, T.riff, T.ding) : [],
    door: interpolate(t, [T.ding + 0.1, T.ding + 0.5], [0, 1], { ...clamp, easing: Easing.out(Easing.back(1.4)) }),
    press: interpolate(t, [T.press - 0.08, T.press, T.press + 0.15], [0, 1, 0], clamp),
    screen: <Footage scene="hero" lang={lang} width={STAGE.screen.w} height={STAGE.screen.h} duration={1} freezeAt={0} cursor={false} />,
    phone: <Chat copy={copy} t={t} T={T} />,
  };
  const cam = cameraAt(t, shotsOf(T, portrait), width, height);
  const inApp = t >= T.riff && t < T.phone2;
  const appStart = Math.round(T.riff * fps);
  const appCut = Math.round(T.copy * fps);
  const dingP = interpolate(t, [T.ding, T.ding + 0.12, T.ding + 0.3], [0, 1.25, 1], clamp);
  const shake = t >= T.ding && t < T.ding + 0.4 ? Math.sin(t * 90) * 14 * (1 - (t - T.ding) / 0.4) : 0;

  return (
    <AbsoluteFill style={{ background: "#000", overflow: "hidden" }}>
      {/* The kitchen, through the camera */}
      {t < T.world && !inApp && (
        <AbsoluteFill style={{ translate: `${shake}px ${shake * 0.6}px`, filter: cam.blur > 0.5 ? `blur(${cam.blur}px)` : undefined }}>
          <div style={{ position: "absolute", left: cam.tx, top: cam.ty, scale: String(cam.s), transformOrigin: "0 0" }}>
            <Kitchen s={kitchen} />
          </div>
        </AbsoluteFill>
      )}

      {/* The app, full frame, while the clock runs in the corner */}
      <AbsoluteFill style={{ scale: String(inApp ? 1 + 0.018 * Math.exp(-(((t - T.riff) % T.beat + T.beat) % T.beat) * 9) : 1) }}>
      <Sequence from={appStart} durationInFrames={appCut - appStart} name="app: create">
        <Footage scene="create" lang={lang} width={width} height={height} range={[1100, 15600]} duration={appCut - appStart} zoom={portrait ? 2.1 : 1.25} />
      </Sequence>
      <Sequence from={appCut} durationInFrames={Math.round(T.phone2 * fps) - appCut} name="app: copy">
        <Footage scene="internet" lang={lang} width={width} height={height} range={[2300, 3700]} duration={Math.round(T.phone2 * fps) - appCut} cams={[{ at: 0, x: 1100, y: 268, zoom: portrait ? 3.2 : 2 }, { at: 1, x: 1250, y: 268, zoom: portrait ? 3.4 : 2.2 }]} />
      </Sequence>
      </AbsoluteFill>
      {inApp && left !== null && (
        <div style={{ position: "absolute", right: portrait ? 40 : 48, top: portrait ? 60 : 40, padding: "14px 22px", borderRadius: 16, background: "rgba(10,22,12,.88)", boxShadow: "0 10px 30px rgba(0,0,0,.5), 0 0 0 2px rgba(124,255,107,.25)", display: "flex", alignItems: "center", gap: 16, scale: String(1 + 0.04 * Math.max(0, Math.sin(t * Math.PI * 2 * 2))) }}>
          <div style={{ width: 34, height: 44, borderRadius: "12px 12px 4px 4px", background: "linear-gradient(90deg, #e9dcc0, #f6efe0 50%, #d9c9a8)", display: "grid", placeItems: "center" }}><div style={{ width: 24, height: 10, background: "#d23b2f", borderRadius: 2 }} /></div>
          <SevenSeg text={mmss(left)} h={54} />
        </div>
      )}

      {/* Words */}
      {t >= T.press + 0.4 && t < T.phone - 0.1 && (
        <div style={{ position: "absolute", left: 0, right: 0, bottom: portrait ? 300 : 90, textAlign: "center", fontFamily: F.display, fontWeight: 900, fontStretch: "118%", fontSize: portrait ? 92 : 110, color: "#fff", letterSpacing: "-0.02em", textShadow: "0 8px 40px rgba(0,0,0,.6)", opacity: interpolate(t, [T.press + 0.4, T.press + 0.6, T.phone - 0.3, T.phone - 0.1], [0, 1, 1, 0], clamp), translate: `0 ${interpolate(t, [T.press + 0.4, T.press + 0.7], [30, 0], clamp)}px` }}>
          {copy.spot.twoMinutes}
        </div>
      )}
      {t >= T.ding && t < T.world && (
        <AbsoluteFill style={{ display: "grid", placeItems: "center" }}>
          <div style={{ fontFamily: F.display, fontWeight: 900, fontStretch: "125%", fontSize: portrait ? 220 : 260, color: "#fff", WebkitTextStroke: "6px #d23b2f", textShadow: "12px 12px 0 #d23b2f", rotate: "-6deg", scale: String(dingP) }}>{copy.spot.ding}</div>
        </AbsoluteFill>
      )}
      <AbsoluteFill style={{ background: "#fff", opacity: interpolate(t, [T.ding, T.ding + 0.04, T.ding + 0.25], [0, 0.85, 0], clamp), pointerEvents: "none" }} />

      <Sequence from={Math.round(T.world * fps)} durationInFrames={Math.round((T.end - T.world) * fps)} name="in the game">
        <World copy={copy} T={T} />
      </Sequence>
      <Sequence from={Math.round(T.end * fps)} name="end card">
        <AbsoluteFill style={{ background: "radial-gradient(ellipse at 50% 40%, #1d3a5c, #0b1622 70%)" }} />
        <EndCard copy={{ ...copy, brand: { ...copy.brand, tagline: copy.spot.tagline } }} credit={T.credit} />
      </Sequence>

      {/* Sound */}
      {[0.35, 0.6, 0.85].map((a) => <Sfx key={a} at={a * fps} name="key" volume={0.9} />)}
      <Sfx at={T.press * fps} name="click" volume={1} />
      {copy.spot.chat.map((_, i) => <Sfx key={`c${i}`} at={(T.phone + 0.25 + i * 0.55) * fps} name="pop" volume={0.6} />)}
      <Sfx at={(T.phone2 + 0.3) * fps} name="pop" volume={0.6} />
      <Sfx at={(T.phone2 + 1.0) * fps} name="pop" volume={0.6} />
      <Sfx at={T.ding * fps} name="microwave" volume={1} />
      {music && <Audio src={staticFile(T.music)} trimBefore={Math.round(T.musicFrom * fps)} volume={(f) => interpolate(f / fps, [T.total - 1.2, T.total], [1, 0], clamp)} />}
    </AbsoluteFill>
  );
};

export const spotDuration = (fps: number) => Math.ceil(SPOT.total * fps);
