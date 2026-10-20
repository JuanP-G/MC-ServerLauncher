import { loadFont as loadFontFile } from "@remotion/fonts";
import { loadFont as loadArchivo } from "@remotion/google-fonts/Archivo";
import { loadFont as loadMono } from "@remotion/google-fonts/JetBrainsMono";
import { staticFile } from "remotion";

// The look: the night sky and grass of the website, the app's own dark panels, and the game's
// letters for the lines that matter. Archivo (wide, heavy) for the stamps, JetBrains Mono for consoles.
export const C = {
  night: "#0b1622",
  skyTop: "#0c1b33",
  skyMid: "#1f4a7a",
  skyLow: "#2c6aa0",
  grass: "#7ccf3a",
  grassDark: "#3c8527",
  ink: "#f3f6f8",
  muted: "#9fb0c0",
  shadow: "#1b1b1b",
  panel: "#121212",
  panelEdge: "#2b2b2b",
  err: "#ff5f56",
  warn: "#e3a82b",
  blue: "#58a6ff",
  chat: "#c792ea",
  inventory: "#c6c6c6",
  terminal: "#07090c",
};

const archivo = loadArchivo("normal", { weights: ["500", "700", "900"], subsets: ["latin", "latin-ext"] });
const mono = loadMono("normal", { weights: ["400", "700"], subsets: ["latin", "latin-ext"] });

export const F = {
  /** The game's letters. Regular only, at multiples of 8 px, or they blur. */
  pixel: "'Minecraft', 'JetBrains Mono', monospace",
  display: `${archivo.fontFamily}, 'Arial Narrow', sans-serif`,
  mono: `${mono.fontFamily}, Consolas, monospace`,
};

loadFontFile({ family: "Minecraft", url: staticFile("brand/minecraft.woff"), format: "woff" });
