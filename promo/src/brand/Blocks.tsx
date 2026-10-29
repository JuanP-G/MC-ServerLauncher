import { Audio } from "@remotion/media";
import React from "react";
import { AbsoluteFill, Easing, Img, interpolate, Sequence, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { AppWindow } from "../components/AppWindow";
import { Footage } from "../components/Footage";
import { ServerEntry } from "../components/Minecraft";
import { Sfx } from "../components/Sfx";
import { BlockText } from "../components/Type";
import { tex } from "../components/World";
import { LANGUAGES } from "../i18n";
import musicEdits from "../music-edits.json";
import { EndCard } from "./EndCard";
import { Sky } from "./Sky";
import { BLOCKS, musicFile, songOf, type BlocksTimeline } from "./timeline";
import { CROWD, FRIENDS, GLOWS, VILLAGE, type Player } from "./village";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const smooth = { ...clamp, easing: Easing.inOut(Easing.cubic) } as const;

export type BlocksProps = { edit: "c45" | "c30"; lang: string; music: boolean };

/** Where the camera looks (world x at the centre of the screen) and how big a block is, over time. */
const cameraAt = (t: number, tl: BlocksTimeline, portrait: boolean) => {
  const k = portrait
    ? [[0, 0.5, 130], [tl.join, 0.5, 130], [tl.join + 0.7, 3, 54], [tl.grow, 3, 38], [tl.pull, 2, 30], [tl.pull + 4.5, 0, 10]]
    : [[0, 0.5, 150], [tl.window[0], 0.5, 150], [tl.window[0] + 0.9, 4.5, 150], [tl.window[1], 4.5, 150], [tl.join + 0.7, 3, 74], [tl.grow, 3, 52], [tl.pull, 2, 42], [tl.pull + 4.5, 0, 16]];
  const times = k.map((p) => p[0]);
  return {
    cx: interpolate(t, times, k.map((p) => p[1]), smooth),
    bp: interpolate(t, times, k.map((p) => p[2]), smooth),
  };
};

/** Day and night: still night until the time-lapse, two quick days during it, then dawn. */
const skyAt = (t: number, tl: BlocksTimeline) => {
  if (t < tl.grow) return { day: 0, warm: 0, arc: 0.2 };
  if (t < tl.pull) {
    const cycles = 0.2 + ((t - tl.grow) / (tl.pull - tl.grow)) * 2;
    const arc = cycles % 1;
    return { day: Math.max(0, Math.sin(Math.PI * arc)) ** 0.6, warm: 0.4 * Math.max(0, 1 - Math.abs(Math.sin(Math.PI * arc)) * 3), arc };
  }
  const k = interpolate(t, [tl.pull, tl.pull + 2.5], [0, 1], smooth);
  return { day: 0.25 * k, warm: 0.85 * k, arc: 0.03 };
};

const PlayerSprite: React.FC<{ p: Player; at: number; t: number; bp: number; sx: (x: number) => number; ground: number; tag: boolean }> = ({ p, at, t, bp, sx, ground, tag }) => {
  const dt = t - at;
  if (dt < 0) return null;
  const pop = interpolate(dt, [0, 0.25], [0, 1], { ...clamp, easing: Easing.out(Easing.back(2)) });
  const tagSize = Math.max(12, Math.round(bp * 0.34));
  return (
    <div style={{ position: "absolute", left: sx(p.x), top: ground - bp * 2, width: bp, height: bp * 2, scale: String(pop), transformOrigin: "50% 100%" }}>
      <Img src={staticFile(`mc/players/${p.skin}.png`)} style={{ width: bp, height: bp * 2, imageRendering: "pixelated" }} />
      {tag && (
        <div
          style={{
            position: "absolute",
            left: "50%",
            bottom: bp * 2 + bp * 0.18,
            translate: "-50% 0",
            fontFamily: "'Minecraft', monospace",
            fontSize: tagSize,
            lineHeight: 1,
            color: "#fff",
            background: "rgba(0,0,0,.42)",
            padding: `${tagSize * 0.18}px ${tagSize * 0.35}px`,
            whiteSpace: "nowrap",
          }}
        >
          {p.name}
        </div>
      )}
    </div>
  );
};

const Caption: React.FC<{ text: string; from: number; to: number; fps: number; size: number; top: number; accent: string[] }> = ({ text, from, to, fps, size, top, accent }) => (
  <Sequence from={Math.round(from * fps)} durationInFrames={Math.round((to - from) * fps)} layout="none" name={`caption ${text}`}>
    <div style={{ position: "absolute", left: 60, right: 60, top, display: "flex", justifyContent: "center" }}>
      <BlockText text={text} size={size} at={0} stagger={0.6} align="center" accent={accent} out={Math.round((to - from) * fps) - 8} />
    </div>
  </Sequence>
);

/** «Bloque a bloque»: one block in the night becomes a house, a village and a community. */
export const Blocks: React.FC<BlocksProps> = ({ edit, lang, music }) => {
  const frame = useCurrentFrame();
  const { fps, width, height } = useVideoConfig();
  const portrait = height > width;
  const tl = BLOCKS[edit];
  const copy = LANGUAGES[lang];
  const t = frame / fps;
  const cam = cameraAt(t, tl, portrait);
  const ground = height * (portrait ? 0.72 : 0.8);
  const sx = (x: number) => width / 2 + (x - cam.cx) * cam.bp;
  const sky = skyAt(t, tl);
  const night = 1 - sky.day;
  const buildAt = (u: number) => tl.join + u * (tl.pull - tl.join);
  const beat = musicEdits[edit].beat;

  // The first block falls from above the screen and lands with a thump.
  const fall = interpolate(t, [tl.block - 0.45, tl.block], [0, 1], { ...clamp, easing: Easing.in(Easing.quad) });
  const squash = interpolate(t, [tl.block, tl.block + 0.08, tl.block + 0.2], [1, 0.86, 1], clamp);

  // How many are on the server: the friends one by one, then the crowd in a rush.
  const friendsIn = FRIENDS.filter((p) => t >= tl.join + p.at * (tl.grow - tl.join)).length;
  const rush = interpolate(t, [tl.grow, tl.pull], [0, 1], { ...clamp, easing: Easing.in(Easing.quad) });
  const online = Math.max(1, friendsIn + Math.round(rush * (187 - FRIENDS.length)));
  const max = online > 90 ? 200 : online > 18 ? 100 : 20;

  // The app's window: up from below, then folded into the server's row of the game's list.
  const [w0, w1] = tl.window;
  const winIn = interpolate(t, [w0, w0 + 0.7], [0, 1], { ...clamp, easing: Easing.out(Easing.cubic) });
  const winOut = interpolate(t, [w1 - 0.5, w1], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const winW = portrait ? 960 : 860;
  const winH = Math.round((winW * 9) / 16);
  const winX = portrait ? (width - winW) / 2 : width - winW - 110;
  const winY = portrait ? height * 0.24 : (height * 0.8 - winH) / 2;
  const entryScale = 1;
  const entryShown = interpolate(t, [w1 - 0.35, w1 + 0.2], [0, 1], clamp);

  const groundRows = Math.ceil((height - ground) / cam.bp) + 1;
  const firstCol = Math.floor(cam.cx - width / 2 / cam.bp) - 1;
  const lastCol = Math.ceil(cam.cx + width / 2 / cam.bp) + 1;
  const credit = `${copy.brand.music}: C418 — ${songOf(edit)} · Music courtesy of Mojang Studios`;
  const capSize = portrait ? 64 : 72;
  const capTop = portrait ? height * 0.085 : height * 0.08;

  return (
    <AbsoluteFill style={{ background: "#000", overflow: "hidden" }}>
      <Sky {...sky} />

      {/* Ground: one grass row, dirt below, as far as the camera sees */}
      <div
        style={{
          position: "absolute",
          left: 0,
          right: 0,
          top: ground,
          height: groundRows * cam.bp,
          backgroundImage: `url(${tex("grass_side")}), url(${tex("dirt")})`,
          backgroundSize: `${cam.bp}px ${cam.bp}px, ${cam.bp}px ${cam.bp}px`,
          backgroundRepeat: "repeat-x, repeat",
          backgroundPosition: `${sx(0)}px 0, ${sx(0)}px ${cam.bp}px`,
          imageRendering: "pixelated",
        }}
      />

      {/* The first block, then the village */}
      {t >= tl.block - 0.45 && (
        <div
          style={{
            position: "absolute",
            left: sx(0),
            top: ground - cam.bp - (1 - fall) * (ground + cam.bp),
            width: cam.bp,
            height: cam.bp * squash,
            translate: `0 ${cam.bp * (1 - squash)}px`,
            backgroundImage: `url(${tex("grass_side")})`,
            backgroundSize: "100% 100%",
            imageRendering: "pixelated",
          }}
        />
      )}
      {VILLAGE.map((b, i) => {
        if (b.x < firstCol || b.x > lastCol) return null;
        const dt = t - buildAt(b.at);
        if (dt < 0) return null;
        const drop = interpolate(dt, [0, 0.14], [0.7, 0], { ...clamp, easing: Easing.in(Easing.quad) });
        return (
          <div
            key={i}
            style={{
              position: "absolute",
              left: sx(b.x),
              top: ground - (b.y + 1 + drop) * cam.bp,
              width: cam.bp + 0.5,
              height: cam.bp + 0.5,
              backgroundImage: `url(${tex(b.tex)})`,
              backgroundSize: "100% 100%",
              imageRendering: "pixelated",
              opacity: interpolate(dt, [0, 0.06], [0, 1], clamp),
            }}
          />
        );
      })}

      {/* People */}
      {FRIENDS.map((p) => (
        <PlayerSprite key={p.name} p={p} at={tl.join + p.at * (tl.grow - tl.join)} t={t} bp={cam.bp} sx={sx} ground={ground} tag />
      ))}
      {CROWD.map((p) => (
        <PlayerSprite key={p.name} p={p} at={tl.grow + p.at * (tl.pull - tl.grow)} t={t} bp={cam.bp} sx={sx} ground={ground} tag={cam.bp > 14} />
      ))}

      {/* Night: everything darker, and the torches and lamps glowing through it */}
      <AbsoluteFill style={{ background: "rgb(4,10,28)", opacity: 0.3 * night, mixBlendMode: "multiply" }} />
      {VILLAGE.filter((b) => GLOWS.has(b.tex) && t >= buildAt(b.at)).map((b, i) => (
        <div
          key={`g${i}`}
          style={{
            position: "absolute",
            left: sx(b.x + 0.5) - cam.bp * 3,
            top: ground - (b.y + 0.5) * cam.bp - cam.bp * 3,
            width: cam.bp * 6,
            height: cam.bp * 6,
            borderRadius: "50%",
            background: "radial-gradient(circle, rgba(255,196,110,.55), rgba(255,170,80,0) 65%)",
            mixBlendMode: "screen",
            opacity: 0.25 + 0.75 * night,
          }}
        />
      ))}

      {/* The app, for one moment */}
      {t >= w0 && t < w1 + 0.05 && (
        <div
          style={{
            position: "absolute",
            left: winX + (portrait ? 0 : 0),
            top: winY + (1 - winIn) * 120,
            width: winW,
            opacity: winIn * (1 - winOut),
            scale: String(1 - 0.75 * winOut),
            transformOrigin: portrait ? "50% 0%" : "100% 0%",
          }}
        >
          <AppWindow width={winW} height={winH}>
            <Sequence from={Math.round(w0 * fps)} layout="none">
              <Footage
                scene="create"
                lang={lang}
                width={winW}
                height={winH}
                range={[10500, 15400]}
                duration={Math.round((w1 - w0) * fps)}
                cams={[{ at: 0, x: 640, y: 420, zoom: 1.25 }, { at: 0.6, x: 700, y: 260, zoom: 1.4 }, { at: 1, x: 700, y: 240, zoom: 1.5 }]}
              />
            </Sequence>
          </AppWindow>
        </div>
      )}
      <div style={{ position: "absolute", right: portrait ? (width - 76 * 8 * entryScale) / 2 : 60, top: portrait ? height * 0.19 : 50, opacity: entryShown, scale: String(0.9 + 0.1 * entryShown), transformOrigin: "100% 0%" }}>
        <ServerEntry name={copy.community.serverName} motd={copy.community.motd} motdColor="#55ff55" players={`${online}/${max}`} scale={entryScale} />
      </div>

      {/* Words */}
      <Caption text={copy.brand.blocks.first} from={0.5} to={w0 + 0.1} fps={fps} size={capSize} top={capTop} accent={[]} />
      <Caption text={copy.brand.blocks.server} from={w0 + 0.3} to={tl.join - 0.1} fps={fps} size={capSize} top={capTop} accent={[]} />
      <Caption text={copy.brand.blocks.friends} from={tl.join + 0.3} to={tl.grow - 0.1} fps={fps} size={capSize} top={capTop} accent={[]} />
      <Caption text={copy.brand.blocks.grow} from={tl.grow + 0.2} to={tl.pull - 0.2} fps={fps} size={capSize} top={capTop} accent={[]} />
      <Caption text={copy.brand.blocks.community} from={tl.pull + 0.5} to={tl.end - 0.1} fps={fps} size={capSize} top={capTop} accent={[]} />

      <Sequence from={Math.round(tl.end * fps)} name="end card">
        <EndCard copy={copy} credit={credit} />
      </Sequence>

      {/* Sound: the thump of the first block, a soft knock on every beat of the build, a pop per friend */}
      <Sfx at={tl.block * fps} name="block" volume={1} />
      {Array.from({ length: Math.floor((tl.pull - tl.join) / beat) }, (_, i) => (
        <Sfx key={`b${i}`} at={(tl.join + i * beat) * fps} name="block" volume={0.18} />
      ))}
      {FRIENDS.map((p) => <Sfx key={`p${p.name}`} at={(tl.join + p.at * (tl.grow - tl.join)) * fps} name="pop" volume={0.35} />)}
      {music && <Audio src={staticFile(musicFile(edit))} />}
    </AbsoluteFill>
  );
};

export const blocksDuration = (edit: "c45" | "c30", fps: number) => Math.ceil(BLOCKS[edit].total * fps);
