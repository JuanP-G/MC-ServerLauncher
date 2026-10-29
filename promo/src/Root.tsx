import React from "react";
import { Composition, Folder } from "remotion";
import { Blocks, blocksDuration } from "./brand/Blocks";
import { Manifesto, manifestoDuration } from "./brand/Manifesto";
import { Edit, type EditProps } from "./Edit";
import { Spot, spotDuration } from "./spot/Spot";
import { LANGUAGES } from "./i18n";
import { durationOf, sizeOf, type EditName } from "./timing";
import "./theme";

const FPS = 30;
const EDITS: EditName[] = ["promo45", "vertical30", "short15"];

/** Every edit in every language: promo45-es, vertical30-en, and so on. */
export const Root: React.FC = () => (
  <>
    {Object.keys(LANGUAGES).map((lang) => (
      <Folder key={lang} name={lang}>
        {EDITS.map((edit) => (
          <Composition
            key={edit}
            id={`${edit}-${lang}`}
            component={Edit as unknown as React.FC<Record<string, unknown>>}
            durationInFrames={durationOf(edit, FPS)}
            fps={FPS}
            {...sizeOf(edit)}
            defaultProps={{ edit, lang } satisfies EditProps}
          />
        ))}
      </Folder>
    ))}
    {Object.keys(LANGUAGES).map((lang) => (
      <Folder key={`spot-${lang}`} name={`spot-${lang}`}>
        {SPOTS.map(({ id, size, music }) => (
          <Composition
            key={`${id}-${music}`}
            id={`${id}-${lang}${music ? "" : "-sinmusica"}`}
            component={Spot as unknown as React.FC<Record<string, unknown>>}
            durationInFrames={spotDuration(FPS)}
            fps={FPS}
            {...size}
            defaultProps={{ lang, music }}
          />
        ))}
      </Folder>
    ))}
    {Object.keys(LANGUAGES).map((lang) => (
      <Folder key={`marca-${lang}`} name={`marca-${lang}`}>
        {BRAND.map(({ id, kind, edit, size, music }) => (
          <Composition
            key={`${id}-${music}`}
            id={`${id}-${lang}${music ? "" : "-sinmusica"}`}
            component={(kind === "blocks" ? Blocks : Manifesto) as unknown as React.FC<Record<string, unknown>>}
            durationInFrames={kind === "blocks" ? blocksDuration(edit as "c45" | "c30", FPS) : manifestoDuration(edit as "b45" | "b30", FPS)}
            fps={FPS}
            {...size}
            defaultProps={{ edit, lang, music }}
          />
        ))}
      </Folder>
    ))}
  </>
);

const WIDE = { width: 1920, height: 1080 };
const TALL = { width: 1080, height: 1920 };

/** The two brand videos: 45 s wide and 30 s tall, the tall ones also without music. */
const BRAND = [
  { id: "bloque45", kind: "blocks", edit: "c45", size: WIDE, music: true },
  { id: "bloque30", kind: "blocks", edit: "c30", size: TALL, music: true },
  { id: "bloque30", kind: "blocks", edit: "c30", size: TALL, music: false },
  { id: "manifiesto45", kind: "manifesto", edit: "b45", size: WIDE, music: true },
  { id: "manifiesto30", kind: "manifesto", edit: "b30", size: TALL, music: true },
  { id: "manifiesto30", kind: "manifesto", edit: "b30", size: TALL, music: false },
] as const;

/** The spot «Antes de que pite»: 30 s wide and tall, the tall one also without the song. */
const SPOTS = [
  { id: "spot30", size: WIDE, music: true },
  { id: "spot30", size: WIDE, music: false },
  { id: "spot30v", size: TALL, music: true },
  { id: "spot30v", size: TALL, music: false },
] as const;
