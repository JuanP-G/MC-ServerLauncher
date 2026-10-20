import React from "react";
import { Easing, Img, interpolate, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import footage from "../footage.json";
import { C } from "../theme";

/** The app window is recorded at this size (in its own units), each frame at twice the pixels. */
export const APP_W = 1600;
export const APP_H = 900;

type Frame = { file: string; at: number; x: number; y: number; click: number };
type Recording = { duration: number; frames: Frame[] };
const INDEX = footage as unknown as Record<string, Recording>;

export const hasFootage = (dir: string) => dir in INDEX;
export const recordingOf = (scene: string, lang: string): { dir: string; rec: Recording } => {
  const dir = hasFootage(`${scene}-${lang}`) ? `${scene}-${lang}` : `${scene}-en`;
  return { dir, rec: INDEX[dir] };
};

/** Where the camera looks: a point of the app and how close, at a time of the shot (0..1). */
export type Cam = { at: number; x: number; y: number; zoom: number };

const frameAt = (rec: Recording, ms: number) => {
  const f = rec.frames;
  let lo = 0;
  let hi = f.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (f[mid].at <= ms) lo = mid;
    else hi = mid - 1;
  }
  return lo;
};

const cursorAt = (rec: Recording, ms: number) => {
  const i = frameAt(rec, ms);
  const a = rec.frames[i];
  const b = rec.frames[Math.min(i + 1, rec.frames.length - 1)];
  const span = Math.max(1, b.at - a.at);
  const k = Math.min(1, Math.max(0, (ms - a.at) / span));
  return { x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k };
};

const lastClickBefore = (rec: Recording, ms: number) => {
  for (let i = frameAt(rec, ms); i >= 0; i--) {
    if (rec.frames[i].click === 1) return rec.frames[i].at;
    if (ms - rec.frames[i].at > 600) break;
  }
  return null;
};

/** The camera following the cursor, smoothed over a second so it glides instead of jerking. */
const followCursor = (rec: Recording, ms: number) => {
  let x = 0;
  let y = 0;
  const n = 9;
  for (let k = 0; k < n; k++) {
    const p = cursorAt(rec, Math.min(rec.duration, Math.max(0, ms + (k - (n - 1) / 2) * 140)));
    x += p.x;
    y += p.y;
  }
  return { x: x / n, y: y / n };
};

export const Cursor: React.FC<{ x: number; y: number; size?: number }> = ({ x, y, size = 26 }) => (
  <svg
    width={size}
    height={size * 1.5}
    viewBox="0 0 16 24"
    style={{ position: "absolute", left: x - 1, top: y - 1, filter: "drop-shadow(0 2px 3px rgba(0,0,0,.55))" }}
  >
    <path d="M1 1 L1 19 L5.6 15 L8.6 22 L11.6 20.7 L8.7 14 L14.6 14 Z" fill="#fff" stroke="#000" strokeWidth="1.3" strokeLinejoin="round" />
  </svg>
);

/**
 * Plays one recording of the real app. `range` picks the stretch of it (in ms) shown over the
 * sequence; the camera follows `cams` or, without them, the cursor.
 */
export const Footage: React.FC<{
  scene: string;
  lang: string;
  width: number;
  height: number;
  range?: [number, number];
  duration: number;
  cams?: Cam[];
  zoom?: number;
  cursor?: boolean;
  freezeAt?: number;
}> = ({ scene, lang, width, height, range, duration, cams, zoom = 1, cursor = true, freezeAt }) => {
  const frame = useCurrentFrame();
  const { rec, dir } = recordingOf(scene, lang);
  const [start, end] = range ?? [0, rec.duration];
  const k = Math.min(1, Math.max(0, frame / Math.max(1, duration - 1)));
  const ms = freezeAt ?? start + (end - start) * k;
  const shown = rec.frames[frameAt(rec, ms)];
  const pointer = cursorAt(rec, ms);

  let look = { ...followCursor(rec, ms), zoom };
  if (cams && cams.length) {
    const at = cams.map((c) => c.at);
    const opt = { extrapolateLeft: "clamp", extrapolateRight: "clamp", easing: Easing.inOut(Easing.cubic) } as const;
    look = cams.length === 1
      ? { x: cams[0].x, y: cams[0].y, zoom: cams[0].zoom }
      : {
          x: interpolate(k, at, cams.map((c) => c.x), opt),
          y: interpolate(k, at, cams.map((c) => c.y), opt),
          zoom: interpolate(k, at, cams.map((c) => c.zoom), opt),
        };
  }

  // Fit the whole window, then zoom; keep the window covering the view wherever it can.
  const fit = Math.min(width / APP_W, height / APP_H);
  const s = fit * look.zoom;
  const cw = APP_W * s;
  const ch = APP_H * s;
  const clamp = (v: number, view: number, content: number) =>
    content <= view ? (view - content) / 2 : Math.min(0, Math.max(view - content, v));
  const tx = clamp(width / 2 - look.x * s, width, cw);
  const ty = clamp(height / 2 - look.y * s, height, ch);

  const clickAt = lastClickBefore(rec, ms);
  const ripple = clickAt === null ? 1 : Math.min(1, (ms - clickAt) / 450);

  return (
    <div style={{ position: "absolute", inset: 0, overflow: "hidden", background: "#000" }}>
      <div style={{ position: "absolute", left: tx, top: ty, width: APP_W, height: APP_H, transformOrigin: "0 0", scale: String(s) }}>
        <Img src={staticFile(`footage/${dir}/${shown.file}`)} style={{ width: APP_W, height: APP_H, display: "block" }} />
        {cursor && ripple < 1 && (
          <div
            style={{
              position: "absolute",
              left: pointer.x - 30 * ripple - 6,
              top: pointer.y - 30 * ripple - 6,
              width: 60 * ripple + 12,
              height: 60 * ripple + 12,
              borderRadius: "50%",
              border: `3px solid ${C.grass}`,
              opacity: 1 - ripple,
            }}
          />
        )}
        {cursor && <Cursor x={pointer.x} y={pointer.y} />}
      </div>
    </div>
  );
};
