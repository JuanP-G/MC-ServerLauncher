import { random } from "remotion";

/**
 * The village of «Bloque a bloque», drawn as text: every letter is a block, rows go from the top
 * down and the last row stands on the grass. Each building goes up bottom row first, left to
 * right, inside its own stretch of the build (0 = the melody arrives, 1 = the camera pulls back).
 */
const TEXTURES: Record<string, string> = {
  P: "oak_planks", s: "spruce_planks", k: "dark_oak_planks", C: "cobblestone", M: "mossy_cobblestone",
  S: "stone_bricks", B: "bricks", G: "glass", t: "torch", d: "oak_door_top", D: "oak_door_bottom",
  L: "oak_log", l: "spruce_log", F: "oak_leaves", W: "white_wool", R: "red_wool", H: "hay_block_side",
  K: "bookshelf", O: "glowstone", g: "grass_side",
};

/** Blocks that light the night around them. */
export const GLOWS = new Set(["torch", "glowstone"]);

type Structure = { x: number; y?: number; build: [number, number]; rows: string[] };

const STRUCTURES: Structure[] = [
  // The tree that grows out of the first block.
  { x: -2, y: 1, build: [0.0, 0.18], rows: [" FFF ", "FFFFF", "FFLFF", "  L  ", "  L  "] },
  // The first house, next to it.
  { x: 3, build: [0.04, 0.4], rows: ["   kkk   ", " kkkkkkk ", "kPPPPPPPk", "lPGPdPGPl", "lPPPDPPPl"] },
  { x: 12, build: [0.3, 0.42], rows: ["t"] },
  // A brick house on the left.
  { x: -13, build: [0.32, 0.6], rows: ["   RRR   ", " RRRRRRR ", "BBBBBBBBB", "BGGBdBGGB", "BBBBDBBBB"] },
  { x: -15, build: [0.5, 0.56], rows: ["t"] },
  // A stone tower on the right, lit at the top.
  { x: 14, build: [0.45, 0.7], rows: [" SOS ", "SSSSS", "SGSGS", "SSSSS", "SGSGS", "SSSSS", "SSdSS", "SSDSS"] },
  // A spruce cabin and a farm.
  { x: 21, build: [0.55, 0.8], rows: ["  lll  ", " sssss ", "lsGsGsl", "lssdssl", "lssDssl"] },
  { x: -22, build: [0.6, 0.72], rows: ["  H  ", " HHH ", "HHHHH"] },
  { x: -31, build: [0.65, 0.92], rows: ["   WWW   ", " WWWWWWW ", "CCCCCCCCC", "CGKCdCKGC", "MCCCDCCCM"] },
  { x: 29, build: [0.7, 0.88], rows: [" FFF ", "FFFFF", "FFLFF", "  L  ", "  L  "] },
  { x: 28, build: [0.78, 0.82], rows: ["t"] },
  { x: -17, build: [0.75, 0.8], rows: ["t"] },
  // Further out, only seen when the camera pulls back.
  { x: 35, build: [0.8, 1], rows: ["   kkkkk   ", " kkkkkkkkk ", "kSSSSSSSSSk", "lSGGSdSGGSl", "lSSSSDSSSSl"] },
  { x: 48, build: [0.82, 1], rows: [" FFF ", "FFFFF", "FFLFF", "  L  ", "  L  "] },
  { x: 54, build: [0.84, 1], rows: ["  sss  ", " sssss ", "BBBBBBB", "BGBdBGB", "BBBDBBB"] },
  { x: -42, build: [0.8, 1], rows: ["  RRRRR  ", " RRRRRRR ", "PPPPPPPPP", "PGPPdPPGP", "PPPPDPPPP"] },
  { x: -50, build: [0.82, 1], rows: [" FFF ", "FFFFF", "FFLFF", "  L  ", "  L  "] },
  { x: -58, build: [0.84, 1], rows: [" SOS ", "SSSSS", "SGSGS", "SSSSS", "SSdSS", "SSDSS"] },
  { x: 62, build: [0.85, 1], rows: [" FFF ", "FFFFF", "FFLFF", "  L  ", "  L  "] },
];

export type Block = { x: number; y: number; tex: string; at: number };

/** Every block of the village with the point of the build (0..1) at which it is placed. */
export const VILLAGE: Block[] = STRUCTURES.flatMap((s) => {
  const cells: { x: number; y: number; tex: string }[] = [];
  const h = s.rows.length;
  for (let r = h - 1; r >= 0; r--) {
    for (let c = 0; c < s.rows[r].length; c++) {
      const tex = TEXTURES[s.rows[r][c]];
      if (tex) cells.push({ x: s.x + c, y: (s.y ?? 0) + (h - 1 - r), tex });
    }
  }
  const [a, b] = s.build;
  return cells.map((cell, i) => ({ ...cell, at: a + ((b - a) * i) / Math.max(1, cells.length) }));
});

export type Player = { skin: string; name: string; x: number; at: number };

const SKINS = ["steve", "alex", "kai", "sunny", "zuri", "ari", "noor", "efe", "makena"];

/** The first friends, by name, as they join (0..1 of the time until the time-lapse). */
export const FRIENDS: Player[] = [
  { skin: "alex", name: "Alex", x: 1.2, at: 0.02 },
  { skin: "kai", name: "Kai", x: -1.4, at: 0.2 },
  { skin: "steve", name: "Steve", x: 9.6, at: 0.38 },
  { skin: "sunny", name: "Sunny", x: -5.5, at: 0.55 },
  { skin: "zuri", name: "Zuri", x: 11.2, at: 0.72 },
  { skin: "noor", name: "Noor", x: -10.5, at: 0.88 },
];

const CROWD_NAMES = [
  "Ari", "Efe", "Makena", "Luna", "Mateo", "Iker", "Nora", "Leo", "Vera", "Hugo", "Inés", "Dani", "Sora", "Theo",
  "Mila", "Pau", "Aitana", "Bruno", "Lia", "Marco", "Noa", "Enzo", "Alba", "Gael", "Irene", "Jon", "Olivia",
];

/** Everyone who turns up during the time-lapse (0..1 from it starting to the pull-back). */
export const CROWD: Player[] = CROWD_NAMES.map((name, i) => ({
  skin: SKINS[i % SKINS.length],
  name,
  x: Math.round((random(`px${i}`) - 0.5) * 120 * 2) / 2,
  at: (i / CROWD_NAMES.length) * 0.95,
}));
