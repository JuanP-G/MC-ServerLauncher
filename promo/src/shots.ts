import type { Cam } from "./components/Footage";
import type { Copy } from "./i18n/types";

export type FeatureKey = keyof Copy["features"];

type Take = { range: [number, number]; cams?: Cam[]; zoom?: number };

/**
 * What each feature shows of its recording: the stretch (in ms of the recording) and where the
 * camera looks, for the long take (8 beats) and the short one (4 beats). Coordinates are the
 * app's own (1600 × 900), read off the recorded frames.
 */
export const SHOTS: Record<FeatureKey, { scene: string; item: string; long: Take; short: Take; portraitZoom?: number }> = {
  create: {
    scene: "create",
    item: "iso_crafting",
    long: {
      range: [2200, 15600],
      cams: [
        { at: 0, x: 700, y: 300, zoom: 1.25 },
        { at: 0.55, x: 900, y: 600, zoom: 1.25 },
        { at: 0.78, x: 800, y: 450, zoom: 1 },
        { at: 1, x: 800, y: 450, zoom: 1 },
      ],
    },
    short: { range: [5200, 15000], cams: [{ at: 0, x: 700, y: 330, zoom: 1.3 }, { at: 0.6, x: 1000, y: 700, zoom: 1.2 }, { at: 0.85, x: 800, y: 450, zoom: 1 }, { at: 1, x: 800, y: 450, zoom: 1 }] },
  },
  mods: {
    scene: "mods",
    item: "enchanted_book",
    long: { range: [2000, 8800], zoom: 1.35 },
    short: { range: [3900, 8800], zoom: 1.45 },
  },
  internet: {
    scene: "internet",
    item: "ender_pearl",
    long: {
      range: [800, 6900],
      cams: [
        { at: 0, x: 900, y: 270, zoom: 1.8 },
        { at: 0.55, x: 1050, y: 270, zoom: 1.8 },
        { at: 0.7, x: 800, y: 400, zoom: 1 },
        { at: 1, x: 800, y: 400, zoom: 1.05 },
      ],
    },
    short: { range: [1000, 4300], cams: [{ at: 0, x: 900, y: 270, zoom: 1.9 }, { at: 1, x: 1100, y: 270, zoom: 1.7 }] },
  },
  bedrock: {
    scene: "bedrock",
    item: "iso_grass",
    long: { range: [1800, 6600], cams: [{ at: 0, x: 1100, y: 250, zoom: 1.6 }, { at: 0.45, x: 1100, y: 250, zoom: 1.6 }, { at: 0.75, x: 900, y: 320, zoom: 1.7 }, { at: 1, x: 900, y: 320, zoom: 1.8 }] },
    short: { range: [2000, 6600], cams: [{ at: 0, x: 1100, y: 250, zoom: 1.6 }, { at: 0.4, x: 1100, y: 250, zoom: 1.6 }, { at: 0.7, x: 900, y: 320, zoom: 1.7 }, { at: 1, x: 900, y: 320, zoom: 1.8 }] },
  },
  players: {
    scene: "players",
    item: "totem",
    long: { range: [500, 4300], cams: [{ at: 0, x: 950, y: 600, zoom: 1.35 }, { at: 0.4, x: 950, y: 650, zoom: 1.35 }, { at: 0.6, x: 700, y: 480, zoom: 1.3 }, { at: 1, x: 700, y: 480, zoom: 1.35 }] },
    short: { range: [700, 4300], cams: [{ at: 0, x: 950, y: 600, zoom: 1.35 }, { at: 0.35, x: 950, y: 650, zoom: 1.35 }, { at: 0.6, x: 700, y: 480, zoom: 1.3 }, { at: 1, x: 700, y: 480, zoom: 1.35 }] },
  },
  backups: {
    scene: "backups",
    item: "iso_chest",
    long: { range: [1300, 7200], zoom: 1.4 },
    short: { range: [1300, 6400], zoom: 1.45 },
  },
  sleep: {
    scene: "sleep",
    item: "moon",
    long: { range: [0, 5900], cams: [{ at: 0, x: 990, y: 230, zoom: 1.3 }, { at: 1, x: 990, y: 250, zoom: 1.35 }] },
    short: { range: [800, 5900], cams: [{ at: 0, x: 990, y: 230, zoom: 1.3 }, { at: 1, x: 990, y: 250, zoom: 1.35 }] },
  },
  console: {
    scene: "console",
    item: "iso_command",
    long: { range: [600, 7700], zoom: 1.45 },
    short: { range: [1200, 7700], zoom: 1.5 },
  },
};

/** Where the window and the words go for the i-th feature, so consecutive ones alternate sides. */
export const featureLayout = (width: number, height: number, index: number) => {
  const portrait = height > width;
  if (portrait) {
    const w = 1000;
    return { portrait, win: { x: (width - w) / 2, y: 760, w, h: 1010 }, text: { x: 70, y: 210, w: width - 140 }, tilt: 0 };
  }
  const w = 1180;
  const h = Math.round((w * 9) / 16);
  const left = index % 2 === 1;
  return {
    portrait,
    win: { x: left ? 90 : width - w - 90, y: (height - h - 102) / 2 - 6, w, h },
    text: { x: left ? 90 + w + 60 : 100, y: 0, w: width - w - 90 - 60 - 100 },
    tilt: left ? 7 : -7,
  };
};
