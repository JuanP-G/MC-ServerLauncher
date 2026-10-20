import { en } from "./en";
import { es } from "./es";
import type { Copy } from "./types";

/** Every language the video is made in. Each one gets its three compositions in the Studio. */
export const LANGUAGES: Record<string, Copy> = { es, en };

/** The footage is recorded per language; a language without its own falls back to English. */
export const footageLang = (lang: string, has: (dir: string) => boolean, scene: string) =>
  has(`${scene}-${lang}`) ? lang : "en";
